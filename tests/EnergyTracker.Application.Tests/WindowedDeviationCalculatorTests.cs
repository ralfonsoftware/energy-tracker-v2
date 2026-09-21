using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class WindowedDeviationCalculatorTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A 73-day elapsed span is deliberately NOT the production ±7-day (14-day-max) window — this
    // test exercises ComputeDeviation's pure arithmetic directly, and 73/365 = 0.2 is the smallest
    // day-count that divides 365 cleanly, so BonusDecayNormalizer's decimal division terminates
    // exactly instead of leaving a repeating-decimal rounding epsilon that would make the
    // exact-tie-at-threshold assertions below flaky. 1000 kWh/year * 0.2 = 200 kWh expected;
    // 100 kWh/year * 0.2 = 20 kWh threshold.
    private static readonly TimeSpan ElapsedSpan = TimeSpan.FromDays(73);
    private const decimal YearlyBaselineKwh = 1000m;
    private const decimal TrendingThresholdKwh = 100m;
    private const decimal ExpectedKwh = 200m;
    private const decimal ThresholdKwh = 20m;

    private static MeterReading Reading(decimal kwhValue, DateTimeOffset readingTimestamp) => new()
    {
        Id = Guid.NewGuid(),
        HouseholdId = Guid.NewGuid(),
        MainMeterId = Guid.NewGuid(),
        KwhValue = kwhValue,
        ReadingTimestamp = readingTimestamp,
        IdempotencyKey = Guid.NewGuid(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static IReadOnlyList<MeterReading> WindowOf(decimal actualConsumedKwh) =>
    [
        Reading(1000m, WindowStart),
        Reading(1000m + actualConsumedKwh, WindowStart + ElapsedSpan),
    ];

    [Fact]
    public void Consumption_well_above_expected_resolves_to_Bump()
    {
        var result = WindowedDeviationCalculator.ComputeDeviation(
            WindowOf(ExpectedKwh + ThresholdKwh + 80m), YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBe(AiPlausibilityDirection.Bump);
    }

    [Fact]
    public void Consumption_well_below_expected_resolves_to_Dip()
    {
        var result = WindowedDeviationCalculator.ComputeDeviation(
            WindowOf(ExpectedKwh - ThresholdKwh - 80m), YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBe(AiPlausibilityDirection.Dip);
    }

    [Fact]
    public void Consumption_matching_the_expected_rate_resolves_to_no_deviation()
    {
        var result = WindowedDeviationCalculator.ComputeDeviation(WindowOf(ExpectedKwh), YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBeNull();
    }

    [Fact]
    public void An_upper_difference_exactly_at_the_threshold_resolves_to_no_deviation_not_Bump()
    {
        // Tie resolves to the calmer "no deviation" state, mirroring
        // PatternDetectiveCalculator.ResolveStatus's own exact-tie precedent.
        var result = WindowedDeviationCalculator.ComputeDeviation(
            WindowOf(ExpectedKwh + ThresholdKwh), YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBeNull();
    }

    [Fact]
    public void A_lower_difference_exactly_at_the_threshold_resolves_to_no_deviation_not_Dip()
    {
        var result = WindowedDeviationCalculator.ComputeDeviation(
            WindowOf(ExpectedKwh - ThresholdKwh), YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBeNull();
    }

    [Fact]
    public void Fewer_than_two_readings_in_the_window_resolves_to_no_deviation()
    {
        var readings = new List<MeterReading> { Reading(1000m, WindowStart) };

        var result = WindowedDeviationCalculator.ComputeDeviation(readings, YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBeNull();
    }

    [Fact]
    public void An_empty_reading_set_resolves_to_no_deviation()
    {
        var result = WindowedDeviationCalculator.ComputeDeviation([], YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBeNull();
    }

    [Fact]
    public void Readings_that_all_share_the_same_timestamp_resolve_to_no_deviation()
    {
        var readings = new List<MeterReading>
        {
            Reading(1000m, WindowStart),
            Reading(1500m, WindowStart),
        };

        var result = WindowedDeviationCalculator.ComputeDeviation(readings, YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBeNull();
    }

    [Fact]
    public void Readings_supplied_out_of_order_are_sorted_before_computing_the_delta()
    {
        var readings = new List<MeterReading>
        {
            Reading(1000m + ExpectedKwh + ThresholdKwh + 80m, WindowStart + ElapsedSpan),
            Reading(1000m, WindowStart),
        };

        var result = WindowedDeviationCalculator.ComputeDeviation(readings, YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBe(AiPlausibilityDirection.Bump);
    }

    [Fact]
    public void An_uncorrected_intermediate_reading_telescopes_to_the_same_delta_as_first_and_last()
    {
        // MeterReading.KwhValue is a cumulative lifetime total, so a pairwise walk across any
        // number of intermediate readings telescopes to the exact same total as a plain
        // last-minus-first subtraction, as long as none of them was a resolved rollover/reset —
        // exactly like PatternDetectiveCalculator.ComputePaceToDate's own cumulative-total walk.
        var readings = new List<MeterReading>
        {
            Reading(1000m, WindowStart),
            Reading(1080m, WindowStart + TimeSpan.FromDays(30)),
            Reading(1000m + ExpectedKwh + ThresholdKwh + 80m, WindowStart + ElapsedSpan),
        };

        var result = WindowedDeviationCalculator.ComputeDeviation(readings, YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBe(AiPlausibilityDirection.Bump);
    }

    // Story 6.3 code review: a resolved Rollover/Reset landing inside the window must not poison
    // the raw delta — mirrors PatternDetectiveCalculator.ComputePaceToDate's own correction tests.
    [Fact]
    public void A_resolved_Rollover_reading_is_corrected_via_its_digit_capacity_not_a_raw_subtraction()
    {
        var previous = Reading(1000m, WindowStart);
        // The meter rolled over: its counter wrapped from near DigitCapacityKwh back to a low
        // value. A raw subtraction (50 - 1000 = -950) would look like a huge, spurious Dip; the
        // correction instead treats it as (DigitCapacityKwh - previous) + current.
        var rolledOver = Reading(50m, WindowStart + ElapsedSpan);
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = Guid.NewGuid(),
            MainMeterId = Guid.NewGuid(),
            MeterReadingId = rolledOver.Id,
            PreviousMeterReadingId = previous.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ResolvedAtUtc = DateTimeOffset.UtcNow,
            Classification = MeterRegressionClassification.Rollover,
            DigitCapacityKwh = 1000m + ExpectedKwh + ThresholdKwh + 80m,
        };
        var resolvedPrompts = new Dictionary<Guid, MeterRegressionPrompt> { [rolledOver.Id] = prompt };

        var result = WindowedDeviationCalculator.ComputeDeviation(
            [previous, rolledOver], YearlyBaselineKwh, TrendingThresholdKwh, resolvedPrompts);

        // Corrected consumption == (DigitCapacityKwh - 1000) + 50 == 350, well above the ~220
        // (expected + threshold) ceiling for this elapsed span — resolves to Bump, not the huge
        // spurious Dip an uncorrected raw subtraction would have produced.
        result.ShouldBe(AiPlausibilityDirection.Bump);
    }

    [Fact]
    public void A_resolved_Reset_pair_contributes_nothing_to_the_delta()
    {
        var previous = Reading(1000m, WindowStart);
        // The meter's counter restarted (Reset) — this pair must be voided entirely, not summed.
        var reset = Reading(5m, WindowStart + TimeSpan.FromDays(1));
        var last = Reading(5m + ExpectedKwh, WindowStart + ElapsedSpan);
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = Guid.NewGuid(),
            MainMeterId = Guid.NewGuid(),
            MeterReadingId = reset.Id,
            PreviousMeterReadingId = previous.Id,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ResolvedAtUtc = DateTimeOffset.UtcNow,
            Classification = MeterRegressionClassification.Reset,
        };
        var resolvedPrompts = new Dictionary<Guid, MeterRegressionPrompt> { [reset.Id] = prompt };

        var result = WindowedDeviationCalculator.ComputeDeviation(
            [previous, reset, last], YearlyBaselineKwh, TrendingThresholdKwh, resolvedPrompts);

        // Only the post-Reset leg (reset -> last, ExpectedKwh consumed) counts, over its own
        // (shorter) elapsed span — close enough to the prorated expected rate to stay within the
        // prorated threshold band, so no deviation. A voided pre-Reset leg would otherwise have
        // pulled the raw last-minus-first delta (ExpectedKwh - 995, a huge spurious Dip) instead.
        result.ShouldBeNull();
    }
}
