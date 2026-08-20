using System.Globalization;
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

    public IReadOnlyList<SmartPlugReading> Parse(Stream fileContent, string fileName, CancellationToken cancellationToken = default)
    {
        using var document = SpreadsheetDocument.Open(fileContent, isEditable: false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException($"'{fileName}' has no workbook part.");
        var workbook = workbookPart.Workbook ?? throw new InvalidOperationException($"'{fileName}' has an empty workbook part.");
        var sheetElement = workbook.Descendants<Sheet>().SingleOrDefault(s => s.Name == Sheet)
            ?? throw new InvalidOperationException($"'{fileName}' has no '{Sheet}' sheet.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetElement.Id!.Value!);
        var worksheet = worksheetPart.Worksheet ?? throw new InvalidOperationException($"'{fileName}' has an empty '{Sheet}' sheet.");
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        var rows = worksheet.Descendants<Row>()
            .OrderBy(r => r.RowIndex?.Value ?? 0)
            .ToList();

        // Row 1 = "Gerät: {device name}", Row 2 = "Raum: {room name}", Row 3 = "Zuhause: ..."
        // (ignored — home identity isn't Household-scoping-relevant), Row 4 = column headers.
        // Data starts at row 5, newest-first.
        var deviceName = StripPrefix(FirstCellText(rows[0], sharedStrings), "Gerät:");
        var roomName = StripPrefix(FirstCellText(rows[1], sharedStrings), "Raum:");

        var readings = new List<SmartPlugReading>();
        foreach (var row in rows.Skip(4))
        {
            // Eve Home files run to tens of thousands of rows — check periodically (not every
            // row) so a shutdown/cancel can interrupt a slow parse without adding per-row overhead.
            if (readings.Count % 500 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var cells = row.Elements<Cell>().ToList();
            if (cells.Count < 2)
            {
                continue;
            }

            var rawTimestamp = CellText(cells[0], sharedStrings);
            // Local time, never UTC-converted (AC #3, AD-9) — deliberate, documented behavior:
            // converting would corrupt data across midnight boundaries. TimeSpan.Zero here is
            // not a UTC claim, just the "apply no conversion" representation for a wire format
            // that requires an explicit offset (project-context.md).
            var timestamp = new DateTimeOffset(
                DateTime.Parse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.None), TimeSpan.Zero);
            // Column is Wh (fractional) — convert to kWh to match SmartPlugReading.KwhValue's unit.
            var kwh = decimal.Parse(CellText(cells[1], sharedStrings), CultureInfo.InvariantCulture) / 1000m;

            readings.Add(new SmartPlugReading
            {
                Id = Guid.NewGuid(),
                HouseholdId = Guid.Empty,
                SmartPlugImportId = Guid.Empty,
                PowerPointId = null,
                RoomName = roomName,
                PowerPointName = deviceName,
                DeviceName = deviceName,
                // ~10-minute samples, not ranged intervals in the source — each row's own
                // timestamp is both the start and end of its interval.
                IntervalStart = timestamp,
                IntervalEnd = timestamp,
                KwhValue = kwh,
            });
        }

        return readings;
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
