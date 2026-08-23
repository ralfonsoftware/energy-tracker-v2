using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-9 vendor adapter. Real layout verified directly against sample-data/eve/*.xlsx during story
// creation (the PRD addendum's cell references were off by one row and omitted the "Zuhause:"
// header entirely) — see Story 3.1 Dev Notes for the full verified layout. Reads the OOXML
// package via DocumentFormat.OpenXml directly rather than ClosedXML: the sample files store
// their date column as the ISO-8601 "t=d" cell type, which ClosedXML 0.105.1 fails to load
// (confirmed empirically — FormatException at workbook-load time, not just cell access).
public class EveHomeXlsxParser : ISmartPlugParser
{
    private const string Sheet = "Gesamtverbrauch";

    public SmartPlugVendorFormat Vendor => SmartPlugVendorFormat.EveHome;

    public bool CanParse(string fileName) => fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    // Story 3.4 AC #1/#5: streams just far enough to read rows 1-2 ("Gerät: {device name}" /
    // "Raum: {room name}") and stops — never advances into the data body starting row 5. Must use
    // the same forward-only OpenXmlReader as Parse, never worksheetPart.Worksheet (that property
    // forces a full-DOM load on first touch regardless of how few rows are read afterward,
    // silently defeating the memory goal even though the row loop itself only reads two rows).
    public string ReadDeviceTag(Stream fileContent, string fileName, CancellationToken cancellationToken = default)
    {
        using var document = SpreadsheetDocument.Open(fileContent, isEditable: false);
        var (worksheetPart, sharedStrings) = OpenWorksheetPart(document, fileName);

        using var reader = OpenXmlReader.Create(worksheetPart);
        string? deviceName = null;
        var rowIndex = 0;
        uint? lastSeenExcelRowIndex = null;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ElementType != typeof(Row) || reader.LoadCurrentElement() is not Row row)
            {
                continue;
            }

            ValidateRowOrder(row, fileName, ref lastSeenExcelRowIndex);
            rowIndex++;

            if (rowIndex == 1)
            {
                deviceName = StripPrefix(FirstCellText(row, sharedStrings), "Gerät:");
                continue;
            }

            if (rowIndex == 2)
            {
                // Row 2 ("Raum:") isn't needed by this call site (ProcessSmartPlugImport
                // resolves room name from the matched Power Point, never from the file itself —
                // Task 5) — read just to match the documented rows-1-2 header-scan bounds, then
                // stop, still well short of row 5's data body.
                break;
            }
        }

        return deviceName ?? throw new InvalidOperationException($"'{fileName}' has no header rows.");
    }

    public SmartPlugParseResult Parse(
        Stream fileContent, string fileName, DateTimeOffset? watermark, CancellationToken cancellationToken = default)
    {
        using var document = SpreadsheetDocument.Open(fileContent, isEditable: false);
        var (worksheetPart, sharedStrings) = OpenWorksheetPart(document, fileName);

        using var reader = OpenXmlReader.Create(worksheetPart);

        // Row 1 = "Gerät: {device name}", Row 2 = "Raum: {room name}", Row 3 = "Zuhause: ..."
        // (ignored — home identity isn't Household-scoping-relevant), Row 4 = column headers.
        // Data starts at row 5, newest-first. A forward-only OpenXmlReader reads rows in raw
        // physical XML order — today's defensive `.OrderBy(RowIndex)` re-sort is deliberately
        // dropped (it would require buffering the whole sheet again, the exact cost this story
        // removes); well-formed .xlsx writers emit sequential RowIndex order, and AC #2's
        // early-stop already assumes trustworthy newest-first document order. In place of the
        // full resort, ValidateRowOrder below fails closed on a missing/misordered RowIndex
        // instead of silently corrupting the header/data boundary or the early-stop assumption.
        string? deviceName = null;
        string? roomName = null;
        var rowIndex = 0;
        var dataRowsSeen = 0;
        uint? lastSeenExcelRowIndex = null;
        var readings = new List<SmartPlugReading>();

        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || reader.LoadCurrentElement() is not Row row)
            {
                continue;
            }

            ValidateRowOrder(row, fileName, ref lastSeenExcelRowIndex);
            rowIndex++;

            if (rowIndex == 1)
            {
                deviceName = StripPrefix(FirstCellText(row, sharedStrings), "Gerät:");
                continue;
            }

            if (rowIndex == 2)
            {
                roomName = StripPrefix(FirstCellText(row, sharedStrings), "Raum:");
                continue;
            }

            if (rowIndex is 3 or 4)
            {
                continue;
            }

            // Eve Home files run to tens of thousands of rows — check periodically (not every
            // row) so a shutdown/cancel can interrupt a slow parse without adding per-row overhead.
            dataRowsSeen++;
            if (dataRowsSeen % 500 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var reading = ParseDataRow(row, sharedStrings, deviceName!, roomName!);
            if (reading is null)
            {
                continue;
            }

            if (watermark is not null && reading.IntervalStart <= watermark)
            {
                // AC #2: rows are confirmed strictly newest-first — stop reading immediately
                // rather than continuing to filter the remaining (already-imported) rows.
                break;
            }

            readings.Add(reading);
        }

        return new SmartPlugParseResult(readings, dataRowsSeen);
    }

    // Story 3.4 review-round-2 patch: shared by ReadDeviceTag and Parse. Fails closed (throws)
    // rather than silently skipping the check when a row has no RowIndex attribute at all — the
    // "well-formed .xlsx writers emit sequential RowIndex order" bet above only holds if RowIndex
    // is actually present to trust in the first place.
    private static void ValidateRowOrder(Row row, string fileName, ref uint? lastSeenExcelRowIndex)
    {
        if (row.RowIndex?.Value is not { } excelRowIndex)
        {
            throw new InvalidOperationException(
                $"'{fileName}' has a row with no RowIndex attribute — the streaming parser requires a " +
                "trustworthy row order (AC #2's early-stop depends on it) and cannot verify it without one.");
        }

        if (lastSeenExcelRowIndex is { } previousExcelRowIndex && excelRowIndex <= previousExcelRowIndex)
        {
            throw new InvalidOperationException(
                $"'{fileName}' rows are not in ascending document order (row {excelRowIndex} follows row " +
                $"{previousExcelRowIndex}) — the streaming parser requires trustworthy row order (AC #2's early-stop depends on it).");
        }

        lastSeenExcelRowIndex = excelRowIndex;
    }

    private static (WorksheetPart worksheetPart, SharedStringTable? sharedStrings) OpenWorksheetPart(
        SpreadsheetDocument document, string fileName)
    {
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException($"'{fileName}' has no workbook part.");
        var workbook = workbookPart.Workbook ?? throw new InvalidOperationException($"'{fileName}' has an empty workbook part.");
        var sheetElement = workbook.Descendants<Sheet>().SingleOrDefault(s => s.Name == Sheet)
            ?? throw new InvalidOperationException($"'{fileName}' has no '{Sheet}' sheet.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetElement.Id!.Value!);

        // Small — string lookups only, not the row data — loaded once up front exactly as today;
        // only the row/data reading needs to become streaming.
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        return (worksheetPart, sharedStrings);
    }

    private static SmartPlugReading? ParseDataRow(Row row, SharedStringTable? sharedStrings, string deviceName, string roomName)
    {
        var cells = row.Elements<Cell>().ToList();
        if (cells.Count < 2)
        {
            return null;
        }

        var rawTimestamp = CellText(cells[0], sharedStrings);
        // Local time, never UTC-converted (AC #3, AD-9) — deliberate, documented behavior:
        // converting would corrupt data across midnight boundaries. TimeSpan.Zero here is not a
        // UTC claim, just the "apply no conversion" representation for a wire format that
        // requires an explicit offset (project-context.md).
        var timestamp = new DateTimeOffset(
            DateTime.Parse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.None), TimeSpan.Zero);
        // Column is Wh (fractional) — convert to kWh to match SmartPlugReading.KwhValue's unit.
        var kwh = decimal.Parse(CellText(cells[1], sharedStrings), CultureInfo.InvariantCulture) / 1000m;

        return new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = Guid.Empty,
            SmartPlugImportId = Guid.Empty,
            PowerPointId = null,
            RoomName = roomName,
            PowerPointName = deviceName,
            DeviceName = deviceName,
            // ~10-minute samples, not ranged intervals in the source — each row's own timestamp
            // is both the start and end of its interval.
            IntervalStart = timestamp,
            IntervalEnd = timestamp,
            KwhValue = kwh,
        };
    }

    private static string FirstCellText(Row row, SharedStringTable? sharedStrings) =>
        CellText(row.Elements<Cell>().First(), sharedStrings);

    private static string CellText(Cell cell, SharedStringTable? sharedStrings)
    {
        var text = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings is not null)
        {
            return sharedStrings.ElementAt(int.Parse(text, CultureInfo.InvariantCulture)).InnerText;
        }

        return text;
    }

    private static string StripPrefix(string cellText, string prefix) =>
        cellText.StartsWith(prefix, StringComparison.Ordinal) ? cellText[prefix.Length..].Trim() : cellText.Trim();
}
