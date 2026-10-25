namespace EnergyTracker.Domain.Calculations;

// Story 11.15 (audit C12, FR-6, review decision 1c): the ONE place the annual Household.TrendingThresholdKwh
// becomes the bar for a span of readings; Status (PatternDetectiveCalculator, GetCurrentStatus's detail
// figure) and the windowed deviation check both call it, so they cannot drift. The proration is
// BonusDecayNormalizer's (AD-5); this adds only the floor.
//
// The floor is one meter step (1 kWh, readings are whole kWh): over a day or two the prorated bar is
// a fraction of a kWh, and rounding noise would decide Trending. It never exceeds the household's own
// annual figure, so a configured 0 still means "any overshoot".
public static class TrendingThreshold
{
    public const decimal MinimumToDateKwh = 1m;

    public static decimal ScaleToDate(decimal annualTrendingThresholdKwh, TimeSpan elapsed)
    {
        var prorated = BonusDecayNormalizer.NormalizeToDate(annualTrendingThresholdKwh, bonusTermsKwh: 0m, elapsed);
        var floor = Math.Min(annualTrendingThresholdKwh, MinimumToDateKwh);

        return Math.Max(floor, prorated);
    }
}
