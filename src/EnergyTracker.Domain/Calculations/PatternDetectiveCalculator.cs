namespace EnergyTracker.Domain.Calculations;

// Distinct from BonusDecayNormalizer, which only normalizes the *comparison target* — this class
// computes the actual gap-tolerant pace itself from a raw MeterReading sequence (AC #1).
public static class PatternDetectiveCalculator
{
    public readonly record struct PaceToDateResult(decimal PaceToDateKwh, TimeSpan Elapsed);

    // AD-12: an open MeterRegressionPrompt excludes its triggering reading, and everything
    // chronologically at or after it (by the same ReadingTimestamp-then-Id order the caller's
    // sequence is already sorted in), from the pace computation until the prompt is resolved.
    public static IReadOnlyList<MeterReading> ExcludeFromOpenPrompt(
        IReadOnlyList<MeterReading> orderedReadings, Guid? triggeringReadingId)
    {
        if (triggeringReadingId is null)
        {
            return orderedReadings;
        }

        var triggeringIndex = -1;
        for (var i = 0; i < orderedReadings.Count; i++)
        {
            if (orderedReadings[i].Id == triggeringReadingId.Value)
            {
                triggeringIndex = i;
                break;
            }
        }

        if (triggeringIndex < 0)
        {
            // The open prompt's triggering reading isn't in its own Main Meter's sequence — a
            // data-integrity violation (e.g. cross-Main-Meter mismatch), not a normal state to
            // silently fall back from, since that would defeat AD-12's exclusion guarantee.
            throw new InvalidOperationException(
                $"Open MeterRegressionPrompt's triggering reading '{triggeringReadingId.Value}' was not found in the provided reading sequence.");
        }

        return orderedReadings.Take(triggeringIndex).ToList();
    }

    private static readonly IReadOnlyDictionary<Guid, MeterRegressionPrompt> NoResolvedPrompts =
        new Dictionary<Guid, MeterRegressionPrompt>();

    // AC #1: walks the ordered (already gap/regression-filtered) reading sequence pairwise — each
    // pair's own gap is absorbed into that pair's own consumption/elapsed contribution, rather
    // than requiring uniform intervals or breaking/resetting the computation on a large gap.
    // MeterReading.KwhValue is a cumulative lifetime total, so this telescopes to the same result
    // as (last - first) — the pairwise walk is what makes the gap-absorption behavior explicit and
    // independently testable per-pair, per AC #1's own wording.
    //
    // Bounded to the trailing 365 days from the most recent reading (confirmed with Ralf, code
    // review of story-2.4) — an unbounded lifetime anchor would let a household's cumulative
    // history dilute Status's sensitivity to recent behavior the longer the product is used.
    //
    // `resolvedPromptsByTriggeringReadingId` keys resolved (Story 2.3) MeterRegressionPrompts by
    // their triggering MeterReadingId — FR-2: once a prompt is resolved, its pair's raw
    // current-previous delta is meaningless and must be corrected (Rollover: digit-capacity
    // offset) or voided (Reset: the meter's counter restarted) rather than left to silently
    // corrupt the total. AD-12's *open*-prompt exclusion (ExcludeFromOpenPrompt) is a separate,
    // earlier step — this only ever sees resolved prompts.
    public static PaceToDateResult? ComputePaceToDate(
        IReadOnlyList<MeterReading> orderedReadings,
        IReadOnlyDictionary<Guid, MeterRegressionPrompt>? resolvedPromptsByTriggeringReadingId = null)
    {
        if (orderedReadings.Count == 0)
        {
            return null;
        }

        var windowStart = orderedReadings[^1].ReadingTimestamp - TimeSpan.FromDays(365);
        var windowed = orderedReadings.Where(r => r.ReadingTimestamp >= windowStart).ToList();

        if (windowed.Count < 2)
        {
            return null;
        }

        var resolvedPrompts = resolvedPromptsByTriggeringReadingId ?? NoResolvedPrompts;

        var totalConsumedKwh = 0m;
        var totalElapsed = TimeSpan.Zero;
        for (var i = 1; i < windowed.Count; i++)
        {
            var previous = windowed[i - 1];
            var current = windowed[i];

            if (resolvedPrompts.TryGetValue(current.Id, out var resolvedPrompt))
            {
                if (resolvedPrompt.Classification == MeterRegressionClassification.Rollover)
                {
                    totalConsumedKwh += (resolvedPrompt.DigitCapacityKwh!.Value - previous.KwhValue) + current.KwhValue;
                    totalElapsed += current.ReadingTimestamp - previous.ReadingTimestamp;
                }

                // Reset: the meter's cumulative counter restarted — this pair contributes
                // nothing; the walk continues cleanly from the next pair (FR-2: "starts a new
                // baseline-computation sequence going forward").
                continue;
            }

            totalConsumedKwh += current.KwhValue - previous.KwhValue;
            totalElapsed += current.ReadingTimestamp - previous.ReadingTimestamp;
        }

        // Every included pair was a voided Reset boundary, or every included reading shares an
        // identical timestamp (e.g. a backfilled batch) — no meaningful rate can be derived;
        // undefined rather than a spurious zero-elapsed distortion (AC #6's "undefined rather
        // than defaulting" principle).
        if (totalElapsed <= TimeSpan.Zero)
        {
            return null;
        }

        return new PaceToDateResult(totalConsumedKwh, totalElapsed);
    }

    // AC #4, #5: strictly-greater-than the threshold resolves to Trending; an exact tie (pace -
    // baseline == threshold) resolves to WithinRange, not Trending — the calmer state. Anything
    // below baseline-to-date (pace - baseline < 0) is BelowBaseline.
    public static Status ResolveStatus(decimal paceToDateKwh, decimal baselineToDateKwh, decimal trendingThresholdKwh)
    {
        var difference = paceToDateKwh - baselineToDateKwh;
        if (difference > trendingThresholdKwh)
        {
            return Status.Trending;
        }

        return difference < 0m ? Status.BelowBaseline : Status.WithinRange;
    }
}
