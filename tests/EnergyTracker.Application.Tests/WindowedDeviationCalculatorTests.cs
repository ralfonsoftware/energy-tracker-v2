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
    public void Uses_only_the_first_and_last_reading_in_the_window_not_intermediate_ones()
    {
        // An intermediate reading (e.g. a lower-value correction) must not change the delta — only
        // first-vs-last matters, exactly like PatternDetectiveCalculator's own cumulative-total
        // assumption.
        var readings = new List<MeterReading>
        {
            Reading(1000m, WindowStart),
            Reading(1080m, WindowStart + TimeSpan.FromDays(30)),
            Reading(1000m + ExpectedKwh + ThresholdKwh + 80m, WindowStart + ElapsedSpan),
        };

        var result = WindowedDeviationCalculator.ComputeDeviation(readings, YearlyBaselineKwh, TrendingThresholdKwh);

        result.ShouldBe(AiPlausibilityDirection.Bump);
    }
}
