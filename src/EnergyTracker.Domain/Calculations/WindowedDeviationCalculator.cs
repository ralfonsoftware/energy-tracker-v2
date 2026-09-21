namespace EnergyTracker.Domain.Calculations;

// Story 6.3 (AC #2, #3, #4) — a windowed sibling of PatternDetectiveCalculator's whole-history
// pace walk: instead of "pace to date across the trailing 365 days from the most recent reading",
// this asks "did consumption deviate meaningfully in a fixed ±7-day window around one specific
// timestamp (an Event's OccurredAt)". AD-14: reads only MeterReading data (the Main Meter is the
// sole authoritative total) — SmartPlugReading is deliberately not summed in here, matching AD-14's
// explicit prohibition on comparing a SmartPlugReading-derived figure against the Main Meter total.
//
// This is new Event-aware code, not a change to Pattern Detective itself — it calls
// BonusDecayNormalizer (AD-5) and reads MeterReading data the same way GetCurrentStatus does, but
// lives outside the 9 files PatternDetectiveDoesNotReferenceSmartPlugOrEventDataTests source-scans,
// so Pattern Detective itself stays exactly as Event-unaware as it was before this story.
public static class WindowedDeviationCalculator
{
    public static readonly TimeSpan WindowRadius = TimeSpan.FromDays(7);

    // AC #2: caller decides which readings fall in the window and passes them in already —
    // mirrors PatternDetectiveCalculator.ComputePaceToDate's "caller supplies the already-windowed
    // sequence" shape rather than this static method reaching for a repository itself.
    //
    // `trendingThresholdKwh` is prorated to the window's own actual elapsed span via the exact same
    // BonusDecayNormalizer.NormalizeToDate call used for the expected-consumption figure below
    // (AD-5 reuse, not a second copy of the proration math) — TrendingThresholdKwh is meaningful on
    // an annual basis (Household.cs), so a ±7-day window's bar must be scaled down from it, not
    // applied at full annual magnitude.
    public static AiPlausibilityDirection? ComputeDeviation(
        IReadOnlyList<MeterReading> readingsInWindow, decimal yearlyBaselineKwh, decimal trendingThresholdKwh)
    {
        if (readingsInWindow.Count < 2)
        {
            // Fewer than two readings in the window means no rate can be derived at all — AC #3's
            // "no corresponding observable deviation", not a spurious zero.
            return null;
        }

        var ordered = readingsInWindow.OrderBy(r => r.ReadingTimestamp).ThenBy(r => r.Id).ToList();
        var first = ordered[0];
        var last = ordered[^1];

        var elapsed = last.ReadingTimestamp - first.ReadingTimestamp;
        if (elapsed <= TimeSpan.Zero)
        {
            // Every reading in the window shares an identical timestamp — no meaningful rate,
            // same "undefined rather than a spurious zero-elapsed distortion" principle
            // PatternDetectiveCalculator.ComputePaceToDate documents for its own analogous case.
            return null;
        }

        var actualConsumedKwh = last.KwhValue - first.KwhValue;
        var expectedKwh = BonusDecayNormalizer.NormalizeToDate(yearlyBaselineKwh, bonusTermsKwh: 0m, elapsed);
        var thresholdForWindow = BonusDecayNormalizer.NormalizeToDate(trendingThresholdKwh, bonusTermsKwh: 0m, elapsed);

        var difference = actualConsumedKwh - expectedKwh;

        // Strictly-greater/-less-than, matching PatternDetectiveCalculator.ResolveStatus's own
        // exact-tie-resolves-to-the-calmer-state precedent: a difference exactly at the threshold
        // is "no deviation", not a deviation.
        if (difference > thresholdForWindow)
        {
            return AiPlausibilityDirection.Bump;
        }

        if (difference < -thresholdForWindow)
        {
            return AiPlausibilityDirection.Dip;
        }

        return null;
    }
}
