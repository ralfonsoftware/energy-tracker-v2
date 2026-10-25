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

    // Story 11.15 (audit C12, FR-6): the threshold is annual, so it is scaled to the elapsed span
    // like the baseline. Whole-day spans (and 182.5 days) keep the proration exact in decimal, so
    // the tie cases are real ties. Baseline is 0, so `paceToDateKwh` is the difference.
    [Theory]
    [InlineData(100, 0, 100, 365, Status.WithinRange)] // full year, factor 1: tie at the threshold is the calmer state
    [InlineData(100.01, 0, 100, 365, Status.Trending)] // full year: one hundredth over (the pre-11.15 behaviour)
    [InlineData(20, 0, 100, 73, Status.WithinRange)] // 73 days, factor 0.2: scaled threshold 20, tie
    [InlineData(20.01, 0, 100, 73, Status.Trending)]
    [InlineData(50, 0, 100, 73, Status.Trending)] // the unscaled rule called this within range
    [InlineData(50, 0, 100, 182.5, Status.WithinRange)] // factor 0.5: scaled 50, tie
    [InlineData(50.01, 0, 100, 182.5, Status.Trending)]
    [InlineData(-0.01, 0, 100, 73, Status.BelowBaseline)] // any shortfall, no threshold band on this side
    [InlineData(-500, 0, 100, 73, Status.BelowBaseline)]
    [InlineData(-0.01, 0, 100, 365, Status.BelowBaseline)]
    [InlineData(-500, 0, 100, 365, Status.BelowBaseline)]
    [InlineData(1, 0, 100, 1, Status.WithinRange)] // 1 day: scaled 0.27, floored to 1 kWh, a 1 kWh tie is the calmer state
    [InlineData(1.01, 0, 100, 1, Status.Trending)] // just over the floor (review decision 1c)
    [InlineData(0.5, 0, 100, 1, Status.WithinRange)] // rounding noise under one meter step is not a trend
    [InlineData(0.51, 0, 0.5, 1, Status.Trending)] // annual 0.5: the floor does not exceed the annual figure
    [InlineData(0, 0, 100, 73, Status.WithinRange)] // pace exactly equal to baseline
    [InlineData(0, 0, 100, 365, Status.WithinRange)]
    [InlineData(0, 0, 0, 73, Status.WithinRange)] // zero threshold: no band at all
    [InlineData(0.01, 0, 0, 73, Status.Trending)]
    public void ResolveStatus_scales_the_annual_threshold_to_the_elapsed_span(
        decimal paceToDateKwh, decimal baselineToDateKwh, decimal annualTrendingThresholdKwh, double elapsedDays, Status expected)
    {
        PatternDetectiveCalculator.ResolveStatus(
            paceToDateKwh, baselineToDateKwh, annualTrendingThresholdKwh, TimeSpan.FromDays(elapsedDays)).ShouldBe(expected);
    }

    // AC #1 "matches WindowedDeviationCalculator": the same readings, baseline, annual threshold and
    // elapsed span give a Bump exactly when Status is Trending and no deviation exactly when it is
    // within range. 73 days keeps the tie exact; 14 days (the production Event window) is checked
    // a hundredth either side, where decimal rounding of 14/365 cannot matter.
    [Theory]
    [InlineData(73, 0.01, true)]
    [InlineData(73, 0, false)]
    [InlineData(73, -0.01, false)]
    [InlineData(14, 0.01, true)]
    [InlineData(14, -0.01, false)]
    public void ResolveStatus_agrees_with_WindowedDeviationCalculator_about_where_the_threshold_sits(
        double elapsedDays, decimal offsetFromThresholdKwh, bool expectedDeviation)
    {
        const decimal yearlyBaselineKwh = 1000m;
        const decimal annualThresholdKwh = 100m;
        var elapsed = TimeSpan.FromDays(elapsedDays);
        var expectedKwh = BonusDecayNormalizer.NormalizeToDate(yearlyBaselineKwh, bonusTermsKwh: 0m, elapsed);
        var scaledThresholdKwh = BonusDecayNormalizer.NormalizeToDate(annualThresholdKwh, bonusTermsKwh: 0m, elapsed);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var readings = new[]
        {
            NewReading(1000m, start),
            NewReading(1000m + expectedKwh + scaledThresholdKwh + offsetFromThresholdKwh, start + elapsed),
        };

        var pace = PatternDetectiveCalculator.ComputePaceToDate(readings);
        pace.ShouldNotBeNull();
        var baselineToDateKwh = BonusDecayNormalizer.NormalizeToDate(yearlyBaselineKwh, bonusTermsKwh: 0m, pace.Value.Elapsed);

        var status = PatternDetectiveCalculator.ResolveStatus(
            pace.Value.PaceToDateKwh, baselineToDateKwh, annualThresholdKwh, pace.Value.Elapsed);
        var deviation = WindowedDeviationCalculator.ComputeDeviation(readings, yearlyBaselineKwh, annualThresholdKwh);

        status.ShouldBe(expectedDeviation ? Status.Trending : Status.WithinRange);
        deviation.ShouldBe(expectedDeviation ? AiPlausibilityDirection.Bump : null);
    }
}
