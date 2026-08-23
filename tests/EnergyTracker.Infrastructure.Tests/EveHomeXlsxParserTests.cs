using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Fixture at sample-data/eve/*.xlsx (copied into the test output directory) — a real Eve Home
// export, trimmed to 50 data rows and with the device/room name anonymized before being committed
// (the original spans months of real household data; see .gitignore). Its exact byte-level layout
// (header rows, cell types, column order) is otherwise untouched and documented in Story 3.1's Dev
// Notes, verified directly during story creation (the PRD addendum's cell references were off by
// one row).
public class EveHomeXlsxParserTests
{
    private static readonly string SampleFilePath =
        Path.Combine(AppContext.BaseDirectory, "sample-data", "eve", "2026-06-20_Steckdose-1_Gesamtverbrauch.xlsx");

    [Theory]
    [InlineData("export.xlsx", true)]
    [InlineData("export.csv", false)]
    [InlineData("export.XLSX", true)]
    public void CanParse_recognizes_only_the_xlsx_extension(string fileName, bool expected)
    {
        var parser = new EveHomeXlsxParser();

        parser.CanParse(fileName).ShouldBe(expected);
    }

    [Fact]
    public void Parse_reads_device_and_room_from_the_header_rows()
    {
        var parser = new EveHomeXlsxParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;

        readings.ShouldNotBeEmpty();
        readings[0].DeviceName.ShouldBe("Steckdose 1");
        readings[0].PowerPointName.ShouldBe("Steckdose 1");
        readings[0].RoomName.ShouldBe("Zimmer 1");
        readings[0].PowerPointId.ShouldBeNull();
    }

    [Fact]
    public void Parse_returns_every_data_row_newest_first_matching_the_source_file()
    {
        var parser = new EveHomeXlsxParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;

        // Data spans rows 5..54 in the trimmed fixture (row 5 newest, capped to 50 rows).
        readings.Count.ShouldBe(50);
        readings[0].IntervalStart.ShouldBeGreaterThan(readings[1].IntervalStart);
        readings[^1].IntervalStart.ShouldBeLessThan(readings[^2].IntervalStart);
    }

    [Fact]
    public void Parse_interprets_timestamps_as_local_time_never_UTC_converted()
    {
        var parser = new EveHomeXlsxParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;

        // Row 5 (newest) in the source file: 2026-06-20 12:00:18. AD-9: the wall-clock value is
        // carried through unchanged, represented with a zero offset rather than shifted to UTC.
        var newest = readings[0];
        newest.IntervalStart.Offset.ShouldBe(TimeSpan.Zero);
        newest.IntervalStart.Year.ShouldBe(2026);
        newest.IntervalStart.Month.ShouldBe(6);
        newest.IntervalStart.Day.ShouldBe(20);
        newest.IntervalStart.Hour.ShouldBe(12);
        newest.IntervalStart.Minute.ShouldBe(0);
        newest.IntervalStart.Second.ShouldBe(18);
        newest.IntervalStart.ShouldBe(newest.IntervalEnd);
    }

    [Fact]
    public void Parse_converts_the_Wh_column_to_kWh()
    {
        var parser = new EveHomeXlsxParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;

        // Row 5's Wh value is 0.82.
        readings[0].KwhValue.ShouldBe(0.00082m);
    }

    [Fact]
    public void Parse_with_a_watermark_returns_only_rows_newer_than_it_and_stops_early()
    {
        // AC #1/#2: strictly newest-first, so setting the watermark at readings[29] (0-indexed)
        // of a full parse must yield exactly the 29 newer rows.
        var parser = new EveHomeXlsxParser();
        IReadOnlyList<SmartPlugReading> fullParse;
        using (var fullStream = File.OpenRead(SampleFilePath))
        {
            fullParse = parser.Parse(fullStream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;
        }

        using var stream = File.OpenRead(SampleFilePath);
        var readings = parser.Parse(
            stream, Path.GetFileName(SampleFilePath), watermark: fullParse[29].IntervalStart, TestContext.Current.CancellationToken).Readings;

        // SmartPlugReading isn't value-comparable (each Parse call mints a fresh Id) — assert on
        // IntervalStart (the field the watermark filters by) in order, not whole-object equality.
        readings.Count.ShouldBe(29);
        readings.Select(r => r.IntervalStart).ShouldBe(fullParse.Take(29).Select(r => r.IntervalStart));
    }

    [Fact]
    public void Parse_with_a_watermark_at_or_after_the_newest_row_returns_zero_rows()
    {
        var parser = new EveHomeXlsxParser();
        IReadOnlyList<SmartPlugReading> fullParse;
        using (var fullStream = File.OpenRead(SampleFilePath))
        {
            fullParse = parser.Parse(fullStream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;
        }

        using var stream = File.OpenRead(SampleFilePath);
        var result = parser.Parse(
            stream, Path.GetFileName(SampleFilePath), watermark: fullParse[0].IntervalStart, TestContext.Current.CancellationToken);

        result.Readings.ShouldBeEmpty();
        // Story 3.4 review-round-2 patch: rows were genuinely read and filtered out by the
        // watermark (a legitimate "nothing new" re-import) — distinct from a corrupt/truncated
        // file that never had any data rows at all (RawDataRowsRead == 0, see the dedicated test
        // below).
        result.RawDataRowsRead.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Parse_reports_zero_RawDataRowsRead_for_a_file_whose_body_has_no_data_rows_at_all()
    {
        // Story 3.4 review-round-2 patch: distinguishes a genuinely corrupt/truncated re-upload
        // (zero rows in the body) from a normal "nothing new" incremental re-import (rows present
        // but all filtered by the watermark) — ProcessSmartPlugImport uses this to still flag the
        // former for review instead of silently marking it Completed.
        var parser = new EveHomeXlsxParser();
        using var stream = BuildSyntheticWorkbook("Steckdose 1", "Zimmer 1", dataRowCount: 0);

        var result = parser.Parse(stream, "synthetic-header-only.xlsx", watermark: null, TestContext.Current.CancellationToken);

        result.Readings.ShouldBeEmpty();
        result.RawDataRowsRead.ShouldBe(0);
    }

    [Fact]
    public void ReadDeviceTag_returns_the_device_tag_without_reading_the_data_body()
    {
        // Task 6: must prove this works with NO valid data body at all, not just that it happens
        // to succeed against a fixture that also has one — a synthetic header-only workbook (zero
        // data rows) is the only way to actually exercise that claim.
        var parser = new EveHomeXlsxParser();
        using var stream = BuildSyntheticWorkbook("Steckdose 1", "Zimmer 1", dataRowCount: 0);

        var deviceTag = parser.ReadDeviceTag(stream, "synthetic-header-only.xlsx", TestContext.Current.CancellationToken);

        deviceTag.ShouldBe("Steckdose 1");
    }

    [Fact]
    public void ReadDeviceTag_throws_when_a_row_has_no_RowIndex_attribute()
    {
        // Story 3.4 review-round-2 patch: fails closed instead of silently trusting document order
        // it can't actually verify.
        var parser = new EveHomeXlsxParser();
        using var stream = BuildSyntheticWorkbookWithoutRowIndex("Steckdose 1", "Zimmer 1");

        Should.Throw<InvalidOperationException>(() =>
            parser.ReadDeviceTag(stream, "no-row-index.xlsx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Parse_throws_when_a_row_has_no_RowIndex_attribute()
    {
        // Story 3.4 review-round-2 patch: same fail-closed guard as ReadDeviceTag above, exercised
        // via Parse's data-body pass instead of the header-only pass.
        var parser = new EveHomeXlsxParser();
        using var stream = BuildSyntheticWorkbookWithoutRowIndex("Steckdose 1", "Zimmer 1");

        Should.Throw<InvalidOperationException>(() =>
            parser.Parse(stream, "no-row-index.xlsx", watermark: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Parse_scales_correctly_over_a_large_synthetic_file_streamed_from_a_watermark()
    {
        // AC #5/#7: the committed fixture is trimmed to 50 rows (real dated exports are
        // gitignored/personal data, unavailable in CI — Task 6). A synthetic in-test workbook is
        // the only way to exercise the streaming parser at a scale the small fixture can't, per
        // Task 6's explicit guidance not to assert on un-observable internals — this asserts
        // correctness of the returned set at scale, both with and without a watermark.
        const int RowCount = 20_000;
        var parser = new EveHomeXlsxParser();

        IReadOnlyList<SmartPlugReading> fullParse;
        using (var stream = BuildSyntheticWorkbook("Big Device", "Big Room", RowCount))
        {
            fullParse = parser.Parse(stream, "synthetic-large.xlsx", watermark: null, TestContext.Current.CancellationToken).Readings;
        }

        fullParse.Count.ShouldBe(RowCount);
        fullParse[0].IntervalStart.ShouldBeGreaterThan(fullParse[^1].IntervalStart);

        var watermark = fullParse[RowCount / 2].IntervalStart;
        using var watermarkStream = BuildSyntheticWorkbook("Big Device", "Big Room", RowCount);
        var incrementalResult = parser.Parse(
            watermarkStream, "synthetic-large.xlsx", watermark, TestContext.Current.CancellationToken);
        var incremental = incrementalResult.Readings;

        incremental.Count.ShouldBe(RowCount / 2);
        incremental.Select(r => r.IntervalStart).ShouldBe(fullParse.Take(RowCount / 2).Select(r => r.IntervalStart));
        // Story 3.4 review-round-2 patch: RawDataRowsRead counts every data-body row the
        // streaming reader iterated over before the watermark early-stop, including the boundary
        // row that triggers the stop (it's read/parsed before its IntervalStart is compared
        // against the watermark) — one more than the count of rows that actually survived the
        // filter. Proves the "rows actually read" signal used to disambiguate a corrupt file from
        // a legitimate nothing-new re-import is itself correct at scale.
        incrementalResult.RawDataRowsRead.ShouldBe(RowCount / 2 + 1);
    }

    // Builds a minimal but structurally valid Eve Home-layout workbook in memory: row 1 "Gerät:",
    // row 2 "Raum:", row 3 "Zuhause:" (ignored by the parser), row 4 column headers (ignored),
    // then `dataRowCount` newest-first data rows starting at row 5. Cells are written as plain
    // string values (no shared string table) — EveHomeXlsxParser.CellText reads any non-shared-
    // string cell's raw InnerText directly, so this round-trips through the real parser correctly.
    private static MemoryStream BuildSyntheticWorkbook(string deviceName, string roomName, int dataRowCount)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: false))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Gesamtverbrauch",
            });

            uint rowIndex = 1;
            sheetData.Append(BuildRow(rowIndex++, $"Gerät: {deviceName}"));
            sheetData.Append(BuildRow(rowIndex++, $"Raum: {roomName}"));
            sheetData.Append(BuildRow(rowIndex++, "Zuhause: Test-Zuhause"));
            sheetData.Append(BuildRow(rowIndex++, "Zeitstempel", "Wh"));

            var newest = new DateTime(2026, 6, 20, 12, 0, 0);
            for (var i = 0; i < dataRowCount; i++)
            {
                var timestamp = newest.AddMinutes(-10 * i);
                var whValue = 500 + i % 250;
                sheetData.Append(BuildRow(
                    rowIndex++, timestamp.ToString("yyyy-MM-dd HH:mm:ss"), whValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static Row BuildRow(uint rowIndex, params string[] cellTexts)
    {
        var row = new Row { RowIndex = rowIndex };
        foreach (var text in cellTexts)
        {
            row.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(text) });
        }

        return row;
    }

    // Same header layout as BuildSyntheticWorkbook, but every row omits the (OOXML-optional)
    // RowIndex attribute — exercises ValidateRowOrder's fail-closed guard (Story 3.4
    // review-round-2 patch).
    private static MemoryStream BuildSyntheticWorkbookWithoutRowIndex(string deviceName, string roomName)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: false))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Gesamtverbrauch",
            });

            sheetData.Append(BuildRowWithoutIndex($"Gerät: {deviceName}"));
            sheetData.Append(BuildRowWithoutIndex($"Raum: {roomName}"));
            sheetData.Append(BuildRowWithoutIndex("Zuhause: Test-Zuhause"));
            sheetData.Append(BuildRowWithoutIndex("Zeitstempel", "Wh"));

            worksheetPart.Worksheet.Save();
            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static Row BuildRowWithoutIndex(params string[] cellTexts)
    {
        var row = new Row();
        foreach (var text in cellTexts)
        {
            row.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(text) });
        }

        return row;
    }
}
