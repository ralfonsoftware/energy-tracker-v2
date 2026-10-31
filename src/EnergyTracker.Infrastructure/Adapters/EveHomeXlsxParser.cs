using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using EnergyTracker.Application;
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

    // Story 11.10 (audit S4): the 20 MB upload cap is on the compressed size, so a small file can
    // decompress to gigabytes. Both limits are sized from the real sample data (the largest sheet is 16.8
    // million characters and 121,644 rows, about 2.3 years of 10-minute samples) at about 3.8x and 3.3x
    // that, so roughly 8 years of one plug; raise them if a household has a longer history.
    internal const long MaxCharactersInPart = 64_000_000;
    internal const int MaxRows = 400_000;

    // A real export row has two cells (timestamp, Wh). A row is read cell by cell, so a hostile row of millions of
    // cells (within the character limit) fails here instead of being built as one huge DOM element.
    internal const int MaxCellsPerRow = 100;

    private readonly long _maxDecompressedBytes;
    private readonly long _maxCharactersInPart;
    private readonly int _maxRows;

    public EveHomeXlsxParser() : this(MaxCharactersInPart, MaxCharactersInPart, MaxRows)
    {
    }

    // Tests pass small limits so a bomb does not have to be 64 million characters, and pull the two size
    // limits apart to prove the backstop alone is an ordinary failure.
    internal EveHomeXlsxParser(long maxDecompressedBytes, long maxCharactersInPart, int maxRows)
    {
        _maxDecompressedBytes = maxDecompressedBytes;
        _maxCharactersInPart = maxCharactersInPart;
        _maxRows = maxRows;
    }

    public SmartPlugVendorFormat Vendor => SmartPlugVendorFormat.EveHome;

    public bool CanParse(string fileName) => fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    // Story 3.4 AC #1/#5: streams just far enough to read rows 1-2 ("Gerät: {device name}" /
    // "Raum: {room name}") and stops — never advances into the data body starting row 5. Must use
    // the same forward-only OpenXmlReader as Parse, never worksheetPart.Worksheet (that property
    // forces a full-DOM load on first touch regardless of how few rows are read afterward,
    // silently defeating the memory goal even though the row loop itself only reads two rows).
    public string ReadDeviceTag(Stream fileContent, string fileName, CancellationToken cancellationToken = default)
    {
        using var document = OpenDocument(fileContent, cancellationToken);
        var (worksheetPart, sharedStrings) = OpenWorksheetPart(document, fileName);

        using var reader = OpenXmlReader.Create(worksheetPart);
        string? deviceName = null;
        var rowIndex = 0;
        uint? lastSeenExcelRowIndex = null;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadRow(reader, fileName) is not { } row)
            {
                continue;
            }

            ValidateRowOrder(row, fileName, ref lastSeenExcelRowIndex);
            rowIndex++;

            if (rowIndex == 1)
            {
                deviceName = RequireDeviceTagWithinBound(StripPrefix(FirstCellText(row, sharedStrings), "Gerät:"), fileName);
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
        using var document = OpenDocument(fileContent, cancellationToken);
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
            if (ReadRow(reader, fileName) is not { } row)
            {
                continue;
            }

            // Every Row element counts, empty ones too: the cap bounds the work a file can cost, not the
            // readings it yields. An incremental import that early-stops at the watermark reads few rows
            // and never nears it; a first import of a very long history is what it protects.
            if (rowIndex + 1 > _maxRows)
            {
                throw new SpreadsheetTooLargeException($"'{fileName}' has more than {_maxRows} rows.");
            }

            ValidateRowOrder(row, fileName, ref lastSeenExcelRowIndex);
            rowIndex++;

            if (rowIndex == 1)
            {
                deviceName = RequireDeviceTagWithinBound(StripPrefix(FirstCellText(row, sharedStrings), "Gerät:"), fileName);
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

            if (watermark is not null && reading.IntervalStart < watermark)
            {
                // AD-22/AC #2: rows are confirmed strictly newest-first — a row strictly older
                // than the watermark means every row at or above the watermark (including every
                // row sharing its exact IntervalStart, AC #7's DST-fold case) has already been
                // emitted above; stop without adding this one.
                break;
            }

            // The row (or rows — AC #7's DST-fold case, more than one row can share the exact
            // watermark IntervalStart) at or above the watermark's IntervalStart is included (not
            // skipped) so the orchestration layer (ProcessSmartPlugImport) can compare against the
            // stored value and detect a vendor-side correction (AD-22). Do NOT break as soon as
            // one such row is seen — a second row sharing that exact IntervalStart must also be
            // read, or AC #7's multi-row DST-fold handling can never fire for this vendor; the loop
            // only stops above, once a row strictly older than the watermark is reached.
            readings.Add(reading);
        }

        return new SmartPlugParseResult(readings, dataRowsSeen);
    }

    private sealed record SheetRow(uint? RowIndex, IReadOnlyList<Cell> Cells);

    // Story 11.10 (code review): one worksheet row, read cell by cell with the forward-only reader. Loading the Row
    // element (the old LoadCurrentElement) built every cell of it as one DOM subtree, which a row of millions of
    // cells inside the character limit turns into a memory bomb; here each Cell is loaded alone and a row past
    // MaxCellsPerRow is rejected after that many. Null when the reader is not at a Row start element (an end
    // element, or any other part of the sheet).
    private static SheetRow? ReadRow(OpenXmlReader reader, string fileName)
    {
        if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
        {
            return null;
        }

        uint? rowIndex = null;
        foreach (var attribute in reader.Attributes)
        {
            if (attribute.LocalName == "r" && uint.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                rowIndex = parsed;
            }
        }

        var cells = new List<Cell>();
        if (reader.ReadFirstChild())
        {
            do
            {
                if (reader.ElementType != typeof(Cell) || !reader.IsStartElement)
                {
                    continue;
                }

                if (cells.Count >= MaxCellsPerRow)
                {
                    throw new SpreadsheetTooLargeException($"'{fileName}' has a row with more than {MaxCellsPerRow} cells.");
                }

                cells.Add((Cell)reader.LoadCurrentElement()!);
            }
            while (reader.ReadNextSibling());
        }

        return new SheetRow(rowIndex, cells);
    }

    // Story 3.4 review-round-2 patch: shared by ReadDeviceTag and Parse. Fails closed (throws)
    // rather than silently skipping the check when a row has no RowIndex attribute at all — the
    // "well-formed .xlsx writers emit sequential RowIndex order" bet above only holds if RowIndex
    // is actually present to trust in the first place.
    private static void ValidateRowOrder(SheetRow row, string fileName, ref uint? lastSeenExcelRowIndex)
    {
        if (row.RowIndex is not { } excelRowIndex)
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

    // The one place a package is opened (ReadDeviceTag and Parse both use it), so the limits cannot be
    // skipped by a new caller. MaxCharactersInPart alone cannot give a recognizable failure: it surfaces as
    // the same XmlException a corrupt file throws, and OpenXml would already have loaded a hostile shared
    // strings table into memory. So the real decompressed size of every entry is counted first, never
    // trusting the declared size, and a package over the limit is a SpreadsheetTooLargeException before
    // OpenXml sees it. MaxCharactersInPart stays set as the backstop: if it ever trips without the
    // pre-scan having tripped, the XmlException is an ordinary generic job failure.
    private SpreadsheetDocument OpenDocument(Stream fileContent, CancellationToken cancellationToken)
    {
        RejectOversizedEntries(fileContent, cancellationToken);
        return SpreadsheetDocument.Open(fileContent, isEditable: false, new OpenSettings { MaxCharactersInPart = _maxCharactersInPart });
    }

    // The real decompressed bytes of ALL entries together are counted against the limit (code review: a per-entry
    // limit lets a small upload hold hundreds of near-limit entries, tens of GB of CPU work). A real export totals
    // about 17 MB at most. Cancellable, since inflating is the one long step before OpenXml sees the package.
    private void RejectOversizedEntries(Stream fileContent, CancellationToken cancellationToken)
    {
        using (var archive = new ZipArchive(fileContent, ZipArchiveMode.Read, leaveOpen: true))
        {
            var buffer = new byte[81920];
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                // A directory entry holds no part (the real Eve exports carry some with a stored size that
                // .NET refuses to open); OpenXml ignores them too.
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                try
                {
                    using var entryStream = entry.Open();
                    int read;
                    while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        total += read;
                        if (total > _maxDecompressedBytes)
                        {
                            throw new SpreadsheetTooLargeException(
                                $"The package decompresses to more than {_maxDecompressedBytes} bytes (reached in entry '{entry.FullName}').");
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    // An entry .NET cannot inflate cannot be loaded by OpenXml either (it reads through the
                    // same ZipArchive), so it is no bomb. The real Eve exports carry two such entries
                    // (docProps/app.xml, core.xml) that the parser never touches. A corrupt part the parser
                    // does need still fails in OpenXml, as it always did.
                }
            }
        }

        fileContent.Position = 0;
    }

    private static (WorksheetPart worksheetPart, IReadOnlyList<string>? sharedStrings) OpenWorksheetPart(
        SpreadsheetDocument document, string fileName)
    {
        var workbookPart = document.WorkbookPart ?? throw new InvalidOperationException($"'{fileName}' has no workbook part.");
        var workbook = workbookPart.Workbook ?? throw new InvalidOperationException($"'{fileName}' has an empty workbook part.");
        var sheetElement = workbook.Descendants<Sheet>().SingleOrDefault(s => s.Name == Sheet)
            ?? throw new InvalidOperationException($"'{fileName}' has no '{Sheet}' sheet.");
        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheetElement.Id!.Value!);

        return (worksheetPart, ReadSharedStrings(workbookPart));
    }

    // Built once per open, item by item with the forward-only reader (never the whole table as a DOM),
    // so a cell's lookup is O(1): SharedStringTable.ElementAt(i) is O(i), which would make a capped
    // 400,000-row file whose cells point into a large table a CPU bomb. Null when the package has no
    // shared strings part, as before.
    private static List<string>? ReadSharedStrings(WorkbookPart workbookPart)
    {
        if (workbookPart.SharedStringTablePart is not { } tablePart)
        {
            return null;
        }

        var strings = new List<string>();
        using var reader = OpenXmlReader.Create(tablePart);
        while (reader.Read())
        {
            if (reader.ElementType == typeof(SharedStringItem) && reader.LoadCurrentElement() is SharedStringItem item)
            {
                strings.Add(item.InnerText);
            }
        }

        return strings;
    }

    private static SmartPlugReading? ParseDataRow(SheetRow row, IReadOnlyList<string>? sharedStrings, string deviceName, string roomName)
    {
        var cells = row.Cells;
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

    private static string FirstCellText(SheetRow row, IReadOnlyList<string>? sharedStrings) =>
        CellText(row.Cells[0], sharedStrings);

    private static string CellText(Cell cell, IReadOnlyList<string>? sharedStrings)
    {
        var text = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings is not null)
        {
            return sharedStrings[int.Parse(text, CultureInfo.InvariantCulture)];
        }

        return text;
    }

    // AD-20 (Story 11.4): the tag is part of the unmapped-reading unique key, so its column is bounded.
    // string.Length counts UTF-16 units, the strictest unit across both providers' column types.
    private static string RequireDeviceTagWithinBound(string deviceTag, string fileName) =>
        deviceTag.Length <= SmartPlugReading.DeviceNameMaxLength
            ? deviceTag
            : throw new DeviceTagTooLongException(
                $"'{fileName}' has a device tag of {deviceTag.Length} characters; the limit is {SmartPlugReading.DeviceNameMaxLength}.");

    private static string StripPrefix(string cellText, string prefix) =>
        cellText.StartsWith(prefix, StringComparison.Ordinal) ? cellText[prefix.Length..].Trim() : cellText.Trim();
}
