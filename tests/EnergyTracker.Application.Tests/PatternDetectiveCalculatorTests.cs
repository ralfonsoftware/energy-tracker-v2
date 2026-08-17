using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class PatternDetectiveCalculatorTests
{
    private static MeterReading NewReading(decimal kwhValue, DateTimeOffset readingTimestamp) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        MainMeterId = Guid.NewGuid(),
        KwhValue = kwhValue,
        ReadingTimestamp = readingTimestamp,
        IdempotencyKey = Guid.NewGuid(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Fewer_than_two_readings_returns_no_result()
    {
        var single = new[] { NewReading(100m, DateTimeOffset.UtcNow) };

        PatternDetectiveCalculator.ComputePaceToDate(single).ShouldBeNull();
        PatternDetectiveCalculator.ComputePaceToDate([]).ShouldBeNull();
    }

    [Fact]
    public void A_multi_day_gap_between_two_readings_is_absorbed_into_the_rate_rather_than_breaking_the_computation()
    {
        var baseline = DateTimeOffset.UtcNow;
        var readings = new[]
        {
            NewReading(1000m, baseline),
            // A 10-day gap — must not throw or reset, just contribute its own delta/elapsed.
            NewReading(1100m, baseline.AddDays(10)),
        };

        var result = PatternDetectiveCalculator.ComputePaceToDate(readings);

        result.ShouldNotBeNull();
        result.Value.PaceToDateKwh.ShouldBe(100m);
        result.Value.Elapsed.ShouldBe(TimeSpan.FromDays(10));
    }

    [Fact]
    public void Multiple_pairs_with_irregular_gaps_sum_consumption_and_elapsed_time_across_every_pair()
    {
        var baseline = DateTimeOffset.UtcNow;
        var readings = new[]
        {
            NewReading(1000m, baseline),
            NewReading(1050m, baseline.AddDays(1)),
            // A large gap in the middle of the sequence.
            NewReading(1300m, baseline.AddDays(15)),
            NewReading(1320m, baseline.AddDays(16)),
        };

        var result = PatternDetectiveCalculator.ComputePaceToDate(readings);

        result.ShouldNotBeNull();
        result.Value.PaceToDateKwh.ShouldBe(320m);
        result.Value.Elapsed.ShouldBe(TimeSpan.FromDays(16));
    }

    [Fact]
    public void Readings_sharing_an_identical_timestamp_produce_an_undefined_result_rather_than_a_zero_elapsed_baseline()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var readings = new[]
        {
            NewReading(1000m, timestamp),
            NewReading(1100m, timestamp),
        };

        PatternDetectiveCalculator.ComputePaceToDate(readings).ShouldBeNull();
    }

    [Fact]
    public void Readings_older_than_a_trailing_year_from_the_latest_reading_are_excluded_from_the_walk()
    {
        var latest = DateTimeOffset.UtcNow;
        var readings = new[]
        {
            NewReading(0m, latest.AddDays(-400)), // outside the trailing-365-day window
            NewReading(1000m, latest.AddDays(-182.5)),
            NewReading(2825m, latest),
        };

        var result = PatternDetectiveCalculator.ComputePaceToDate(readings);

        result.ShouldNotBeNull();
        result.Value.PaceToDateKwh.ShouldBe(1825m);
        result.Value.Elapsed.ShouldBe(TimeSpan.FromDays(182.5));
    }

    [Fact]
    public void A_resolved_rollover_prompt_corrects_its_pair_using_the_prompts_digit_capacity()
    {
        var baseline = DateTimeOffset.UtcNow.AddDays(-10);
        var previous = NewReading(9990m, baseline);
        var current = NewReading(10m, baseline.AddDays(10));
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = Guid.NewGuid(),
            MainMeterId = Guid.NewGuid(),
            MeterReadingId = current.Id,
            PreviousMeterReadingId = previous.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Classification = MeterRegressionClassification.Rollover,
            DigitCapacityKwh = 10000m,
            ResolvedAtUtc = DateTimeOffset.UtcNow,
        };

        var result = PatternDetectiveCalculator.ComputePaceToDate(
            [previous, current],
            new Dictionary<Guid, MeterRegressionPrompt> { [current.Id] = prompt });

        result.ShouldNotBeNull();
        result.Value.PaceToDateKwh.ShouldBe(20m); // (10000 - 9990) + 10
        result.Value.Elapsed.ShouldBe(TimeSpan.FromDays(10));
    }

    [Fact]
    public void A_resolved_reset_prompt_voids_its_pair_entirely()
    {
        var baseline = DateTimeOffset.UtcNow.AddDays(-20);
        var beforeReset = NewReading(9990m, baseline);
        var afterReset = NewReading(10m, baseline.AddDays(10));
        var later = NewReading(30m, baseline.AddDays(20));
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = Guid.NewGuid(),
            MainMeterId = Guid.NewGuid(),
            MeterReadingId = afterReset.Id,
            PreviousMeterReadingId = beforeReset.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Classification = MeterRegressionClassification.Reset,
            ResolvedAtUtc = DateTimeOffset.UtcNow,
        };

        var result = PatternDetectiveCalculator.ComputePaceToDate(
            [beforeReset, afterReset, later],
            new Dictionary<Guid, MeterRegressionPrompt> { [afterReset.Id] = prompt });

        result.ShouldNotBeNull();
        result.Value.PaceToDateKwh.ShouldBe(20m); // only (afterReset, later) counts
        result.Value.Elapsed.ShouldBe(TimeSpan.FromDays(10)); // the voided pair's elapsed doesn't count either
    }

    [Fact]
    public void ExcludeFromOpenPrompt_with_no_triggering_id_returns_the_sequence_unchanged()
    {
        var readings = new[] { NewReading(100m, DateTimeOffset.UtcNow) };

        var result = PatternDetectiveCalculator.ExcludeFromOpenPrompt(readings, null);

        result.ShouldBe(readings);
    }

    [Fact]
    public void ExcludeFromOpenPrompt_excludes_the_triggering_reading_and_everything_chronologically_after_it()
    {
        var baseline = DateTimeOffset.UtcNow;
        var first = NewReading(1000m, baseline);
        var second = NewReading(1100m, baseline.AddDays(1));
        var triggering = NewReading(50m, baseline.AddDays(2));
        var afterTriggering = NewReading(60m, baseline.AddDays(3));
        var ordered = new[] { first, second, triggering, afterTriggering };

        var result = PatternDetectiveCalculator.ExcludeFromOpenPrompt(ordered, triggering.Id);

        result.ShouldBe([first, second]);
    }

    [Fact]
    public void ExcludeFromOpenPrompt_throws_when_the_triggering_reading_is_not_in_the_sequence()
    {
        var readings = new[] { NewReading(100m, DateTimeOffset.UtcNow) };

        Should.Throw<InvalidOperationException>(() => PatternDetectiveCalculator.ExcludeFromOpenPrompt(readings, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(100, 0, 100, Status.WithinRange)] // exact tie at the threshold — the calmer state (AC #5)
    [InlineData(100.01, 0, 100, Status.Trending)] // one cent over the threshold (AC #4)
    [InlineData(0, 0, 100, Status.WithinRange)] // pace exactly equal to baseline
    [InlineData(-0.01, 0, 100, Status.BelowBaseline)]
    public void ResolveStatus_applies_the_strictly_greater_than_threshold_rule(
        decimal paceToDateKwh, decimal baselineToDateKwh, decimal trendingThresholdKwh, Status expected)
    {
        PatternDetectiveCalculator.ResolveStatus(paceToDateKwh, baselineToDateKwh, trendingThresholdKwh).ShouldBe(expected);
    }
}
