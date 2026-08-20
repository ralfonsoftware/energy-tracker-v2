using EnergyTracker.Infrastructure.Adapters;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Real fixture at sample-data/meross/*.csv (copied into the test output directory) — its exact
// byte layout (UTF-8 BOM, "\t," field delimiter, trailing tab) is documented in Story 3.1's Dev
// Notes and verified directly during story creation.
public class MerossCsvParserTests
{
    private static readonly string SampleFilePath = Path.Combine(
        AppContext.BaseDirectory, "sample-data", "meross", "Power Monitor Day Data - Schreibtisch - 20260620.csv");

    [Theory]
    [InlineData("Power Monitor Day Data - Schreibtisch - 20260620.csv", true)]
    [InlineData("Power Monitor Day Data - Couch Verteiler - 20260620.csv", true)]
    [InlineData("random-export.csv", false)]
    [InlineData("Power Monitor Day Data - Schreibtisch - 20260620.xlsx", false)]
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

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), TestContext.Current.CancellationToken);

        readings.ShouldNotBeEmpty();
        readings[0].DeviceName.ShouldBe("Schreibtisch");
        readings[0].PowerPointName.ShouldBe("Schreibtisch");
        readings[0].RoomName.ShouldBe(string.Empty);
        readings[0].PowerPointId.ShouldBeNull();
    }

    [Fact]
    public void Parse_returns_one_row_per_day_ascending_matching_the_source_file()
    {
        var parser = new MerossCsvParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), TestContext.Current.CancellationToken);

        // 2026-01-01 .. 2026-06-20 inclusive, ascending in the source file.
        readings.Count.ShouldBe(171);
        readings[0].IntervalStart.ShouldBeLessThan(readings[1].IntervalStart);
        readings[0].KwhValue.ShouldBe(1.492m);
        readings[^1].KwhValue.ShouldBe(0.240m);
    }

    [Fact]
    public void Parse_spans_each_row_over_its_full_day_as_local_time_never_UTC_converted()
    {
        var parser = new MerossCsvParser();
        using var stream = File.OpenRead(SampleFilePath);

        var readings = parser.Parse(stream, Path.GetFileName(SampleFilePath), TestContext.Current.CancellationToken);

        var first = readings[0];
        first.IntervalStart.Offset.ShouldBe(TimeSpan.Zero);
        first.IntervalStart.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        first.IntervalEnd.ShouldBe(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero).AddTicks(-1));
    }
}
