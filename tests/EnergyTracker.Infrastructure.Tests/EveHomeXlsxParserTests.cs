using EnergyTracker.Infrastructure.Adapters;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Real fixture at sample-data/eve/*.xlsx (copied into the test output directory) — its exact
// byte-level layout is documented in Story 3.1's Dev Notes and verified directly during story
// creation (the PRD addendum's cell references were off by one row).
public class EveHomeXlsxParserTests
{
    private static readonly string SampleFilePath =
        Path.Combine(AppContext.BaseDirectory, "sample-data", "eve", "2026-06-20_Steckdose_Tur_Gesamtverbrauch.xlsx");

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

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), TestContext.Current.CancellationToken);

        readings.ShouldNotBeEmpty();
        readings[0].DeviceName.ShouldBe("Steckdose Tür");
        readings[0].PowerPointName.ShouldBe("Steckdose Tür");
        readings[0].RoomName.ShouldBe("Wohnzimmer");
        readings[0].PowerPointId.ShouldBeNull();
    }

    [Fact]
    public void Parse_returns_every_data_row_newest_first_matching_the_source_file()
    {
        var parser = new EveHomeXlsxParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), TestContext.Current.CancellationToken);

        // Data spans rows 5..57184 in the source workbook (row 5 newest).
        readings.Count.ShouldBe(57180);
        readings[0].IntervalStart.ShouldBeGreaterThan(readings[1].IntervalStart);
        readings[^1].IntervalStart.ShouldBeLessThan(readings[^2].IntervalStart);
    }

    [Fact]
    public void Parse_interprets_timestamps_as_local_time_never_UTC_converted()
    {
        var parser = new EveHomeXlsxParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), TestContext.Current.CancellationToken);

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

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), TestContext.Current.CancellationToken);

        // Row 5's Wh value is 0.82.
        readings[0].KwhValue.ShouldBe(0.00082m);
    }
}
