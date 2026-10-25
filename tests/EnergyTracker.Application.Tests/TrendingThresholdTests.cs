using EnergyTracker.Domain.Calculations;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class TrendingThresholdTests
{
    // Story 11.15 review (decision 1c, 2026-10-25): the annual threshold is prorated to the span the
    // readings cover, but never below one meter step (1 kWh) so rounding noise on a short span cannot
    // decide Trending. Whole-day spans keep the proration exact in decimal.
    [Theory]
    [InlineData(100, 365, 100)] // full year: the annual value
    [InlineData(100, 73, 20)] // factor 0.2, above the floor: plain proration
    [InlineData(100, 182.5, 50)]
    [InlineData(100, 5, 1.36986301369863)] // 100 x 5/365, still above the floor
    [InlineData(100, 3, 1)] // 100 x 3/365 = 0.82, floored to 1 kWh
    [InlineData(100, 1, 1)] // 0.27, floored
    [InlineData(100, 0, 1)] // nothing elapsed: still the floor, never zero
    [InlineData(250, 365, 250)]
    public void The_annual_threshold_is_prorated_to_the_span_and_floored_at_one_kWh(
        decimal annualKwh, double elapsedDays, decimal expected)
    {
        TrendingThreshold.ScaleToDate(annualKwh, TimeSpan.FromDays(elapsedDays)).ShouldBe(expected, 0.0000001m);
    }

    // The floor never raises the bar above the household's own annual figure: a configured 0 stays
    // "any overshoot", and an annual 0.5 never becomes 1.
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0, 365, 0)]
    [InlineData(0.5, 1, 0.5)]
    [InlineData(0.5, 365, 0.5)]
    public void The_floor_never_exceeds_the_configured_annual_threshold(decimal annualKwh, double elapsedDays, decimal expected)
    {
        TrendingThreshold.ScaleToDate(annualKwh, TimeSpan.FromDays(elapsedDays)).ShouldBe(expected);
    }

    [Fact]
    public void A_negative_span_is_rejected_like_the_normalizer_rejects_it()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => TrendingThreshold.ScaleToDate(100m, TimeSpan.FromDays(-1)));
    }
}
