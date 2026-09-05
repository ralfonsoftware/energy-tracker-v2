using EnergyTracker.Domain;
using EnergyTracker.Infrastructure.Adapters;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Fixture at sample-data/meross/*.csv (copied into the test output directory) — a real Meross
// export, trimmed to 50 data rows and with the device name (carried in the filename, AC #4)
// anonymized before being committed (the original spans ~6 months of real household data; see
// .gitignore). Its exact byte layout (UTF-8 BOM, "\t," field delimiter, trailing tab) is otherwise
// untouched and documented in Story 3.1's Dev Notes, verified directly during story creation.
public class MerossCsvParserTests
{
    private static readonly string SampleFilePath = Path.Combine(
        AppContext.BaseDirectory, "sample-data", "meross", "Power Monitor Day Data - Verbraucher 1 - 20260620.csv");

    [Theory]
    [InlineData("Power Monitor Day Data - Verbraucher 1 - 20260620.csv", true)]
    [InlineData("Power Monitor Day Data - Verbraucher 2 - 20260620.csv", true)]
    [InlineData("random-export.csv", false)]
    [InlineData("Power Monitor Day Data - Verbraucher 1 - 20260620.xlsx", false)]
    public void CanParse_recognizes_only_the_documented_filename_pattern(string fileName, bool expected)
    {
        var parser = new MerossCsvParser();

        parser.CanParse(fileName).ShouldBe(expected);
    }

    [Fact]
    public void Parse_derives_the_device_tag_from_the_filename_not_the_file_body()
    {
        var parser = new MerossCsvParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;

        readings.ShouldNotBeEmpty();
        readings[0].DeviceName.ShouldBe("Verbraucher 1");
        readings[0].PowerPointName.ShouldBe("Verbraucher 1");
        readings[0].RoomName.ShouldBe(string.Empty);
        readings[0].PowerPointId.ShouldBeNull();
    }

    [Fact]
    public void Parse_returns_one_row_per_day_ascending_matching_the_source_file()
    {
        var parser = new MerossCsvParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;

        // 2026-01-01 .. 2026-02-19 inclusive, ascending, capped to 50 rows in the trimmed fixture.
        readings.Count.ShouldBe(50);
        readings[0].IntervalStart.ShouldBeLessThan(readings[1].IntervalStart);
        readings[0].KwhValue.ShouldBe(1.492m);
        readings[^1].KwhValue.ShouldBe(0.748m);
    }

    [Fact]
    public void Parse_spans_each_row_over_its_full_day_as_local_time_never_UTC_converted()
    {
        var parser = new MerossCsvParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken).Readings;

        var first = readings[0];
        first.IntervalStart.Offset.ShouldBe(TimeSpan.Zero);
        first.IntervalStart.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        first.IntervalEnd.ShouldBe(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).AddTicks(-1));
    }

    [Fact]
    public void Parse_with_a_watermark_includes_the_boundary_row_and_still_reads_every_row()
    {
        // AD-22/AC #3: filtered, not early-stopped — every row is still read (there's no
        // early-stop behavior to verify for Meross, unlike Eve Home), only the returned list is
        // filtered. The row exactly at the watermark's day is now included (not skipped).
        var parser = new MerossCsvParser();
        IReadOnlyList<SmartPlugReading> fullParse;
        int fullRawRowsRead;
        using (var fullStream = File.OpenRead(SampleFilePath))
        {
            var fullResult = parser.Parse(fullStream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken);
            fullParse = fullResult.Readings;
            fullRawRowsRead = fullResult.RawDataRowsRead;
        }

        using var stream = File.OpenRead(SampleFilePath);
        var result = parser.Parse(
            stream, Path.GetFileName(SampleFilePath), watermark: fullParse[9].IntervalStart, TestContext.Current.CancellationToken);

        result.Readings.Count.ShouldBe(fullParse.Count - 9);
        result.Readings.Select(r => r.IntervalStart).ShouldBe(fullParse.Skip(9).Select(r => r.IntervalStart));
        // Story 3.4 review-round-2 patch: RawDataRowsRead counts every row Meross's no-early-stop
        // filter still reads, so it's unaffected by the watermark and matches the unfiltered pass.
        result.RawDataRowsRead.ShouldBe(fullRawRowsRead);
    }

    [Fact]
    public void Parse_reports_zero_RawDataRowsRead_for_a_file_with_only_a_header_line()
    {
        // Story 3.4 review-round-2 patch: distinguishes a genuinely corrupt/truncated re-upload
        // (zero data rows in the body) from a normal "nothing new" incremental re-import.
        var parser = new MerossCsvParser();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Date\t,Power Consumption-(kWh)\t\n"));

        var result = parser.Parse(stream, Path.GetFileName(SampleFilePath), watermark: null, TestContext.Current.CancellationToken);

        result.Readings.ShouldBeEmpty();
        result.RawDataRowsRead.ShouldBe(0);
    }

    [Fact]
    public void ReadDeviceTag_derives_the_device_tag_from_the_filename_without_needing_the_file_body()
    {
        var parser = new MerossCsvParser();
        using var stream = new MemoryStream();

        var deviceTag = parser.ReadDeviceTag(stream, Path.GetFileName(SampleFilePath), TestContext.Current.CancellationToken);

        deviceTag.ShouldBe("Verbraucher 1");
    }
}
