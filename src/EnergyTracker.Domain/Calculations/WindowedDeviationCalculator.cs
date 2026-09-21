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

    private static readonly IReadOnlyDictionary<Guid, MeterRegressionPrompt> NoResolvedPrompts =
        new Dictionary<Guid, MeterRegressionPrompt>();

    // AC #2: caller decides which readings fall in the window and passes them in already —
    // mirrors PatternDetectiveCalculator.ComputePaceToDate's "caller supplies the already-windowed
    // sequence" shape rather than this static method reaching for a repository itself.
    //
    // `trendingThresholdKwh` is prorated to the window's own actual elapsed span via the exact same
    // BonusDecayNormalizer.NormalizeToDate call used for the expected-consumption figure below
    // (AD-5 reuse, not a second copy of the proration math) — TrendingThresholdKwh is meaningful on
    // an annual basis (Household.cs), so a ±7-day window's bar must be scaled down from it, not
    // applied at full annual magnitude.
    //
    // `resolvedPromptsByTriggeringReadingId` mirrors PatternDetectiveCalculator.ComputePaceToDate's
    // own parameter of the same name/shape: a resolved (Story 2.3) MeterRegressionPrompt's raw
    // current-previous delta is meaningless and must be corrected (Rollover) or voided (Reset),
    // exactly like the trailing-365-day pace walk does — a meter rollover/reset landing inside an
    // Event's ±7-day window would otherwise poison this first/last delta into a spurious Bump/Dip.
    // (AD-12's *open*-prompt exclusion is a separate, earlier concern — deliberately still deferred,
    // see deferred-work.md — this only ever sees resolved prompts.)
    public static AiPlausibilityDirection? ComputeDeviation(
        IReadOnlyList<MeterReading> readingsInWindow,
        decimal yearlyBaselineKwh,
        decimal trendingThresholdKwh,
        IReadOnlyDictionary<Guid, MeterRegressionPrompt>? resolvedPromptsByTriggeringReadingId = null)
    {
        if (readingsInWindow.Count < 2)
        {
            // Fewer than two readings in the window means no rate can be derived at all — AC #3's
            // "no corresponding observable deviation", not a spurious zero.
            return null;
        }

        var ordered = readingsInWindow.OrderBy(r => r.ReadingTimestamp).ThenBy(r => r.Id).ToList();
        var resolvedPrompts = resolvedPromptsByTriggeringReadingId ?? NoResolvedPrompts;

        // Pairwise walk, not a plain last-first subtraction — telescopes to the identical result
        // when no resolved prompt intersects the window (MeterReading.KwhValue is a cumulative
        // lifetime total), but correctly absorbs a Rollover's digit-capacity offset or voids a
        // Reset pair when one does, exactly like ComputePaceToDate's own walk.
        var actualConsumedKwh = 0m;
        var elapsed = TimeSpan.Zero;
        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];

            if (resolvedPrompts.TryGetValue(current.Id, out var resolvedPrompt))
            {
                if (resolvedPrompt.Classification == MeterRegressionClassification.Rollover)
                {
                    actualConsumedKwh += (resolvedPrompt.DigitCapacityKwh!.Value - previous.KwhValue) + current.KwhValue;
                    elapsed += current.ReadingTimestamp - previous.ReadingTimestamp;
                }

                // Reset: the meter's cumulative counter restarted — this pair contributes nothing.
                continue;
            }

            actualConsumedKwh += current.KwhValue - previous.KwhValue;
            elapsed += current.ReadingTimestamp - previous.ReadingTimestamp;
        }

        if (elapsed <= TimeSpan.Zero)
        {
            // Every reading in the window shares an identical timestamp, or every pair was a
            // voided Reset boundary — no meaningful rate, same "undefined rather than a spurious
            // zero-elapsed distortion" principle PatternDetectiveCalculator.ComputePaceToDate
            // documents for its own analogous case.
            return null;
        }

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
