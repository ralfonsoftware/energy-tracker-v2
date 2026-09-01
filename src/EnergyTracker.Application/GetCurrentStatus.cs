using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;

namespace EnergyTracker.Application;

public record CurrentStatusResult(
    Status Status,
    decimal PaceToDateKwh,
    decimal BaselineToDateKwh,
    bool IsLowConfidence,
    double ElapsedDays,
    decimal TrendingThresholdKwh,
    double DaysSinceLastReading,
    int LowConfidenceGapDaysThreshold);

/// <summary>Computes the caller's Household's current Status live, synchronously, at request time — undefined (null) with fewer than two Readings or no Yearly Baseline set (AC #1, #2, #3, #6, #9, #10; AD-7, AD-12, AD-14).</summary>
public class GetCurrentStatus(
    IHouseholdRepository householdRepository,
    IMeterReadingRepository readingRepository,
    IMeterRegressionPromptRepository regressionPromptRepository,
    ISmartPlugCoverageSignal smartPlugCoverageSignal)
{
    // PatternDetectiveCalculator.ComputePaceToDate only ever windows the trailing 365 days from
    // the last included reading (see its own doc comment) — 400 gives that a 35-day margin so an
    // exact-boundary reading is never clipped. Shared here instead of a literal duplicated across
    // this call site and its tests.
    private const int RecentReadingWindowDays = 400;

    // AD-7: this is the single place the live computation runs — both the GET /api/status read
    // path and IStatusRecomputeService's snapshot-writing path call this same method, so the two
    // can never disagree on exclusion/threshold logic (Task 6's own requirement).
    //
    // asOfUtc (Story 4.3): when supplied, computes Status as it would have looked at that
    // historical wall-clock moment instead of live/now — used by
    // IStatusRecomputeService.RecomputeForwardFromAsync to regenerate historical StatusSnapshot
    // points after a Meter Reading correction. Must go after cancellationToken (C# requires
    // optional parameters after all required ones); every existing 2-arg call site is unaffected.
    public async Task<CurrentStatusResult?> ExecuteAsync(Guid householdId, CancellationToken cancellationToken, DateTimeOffset? asOfUtc = null)
    {
        var household = await householdRepository.FindByIdAsync(householdId, cancellationToken);
        if (household?.YearlyBaselineKwh is not { } yearlyBaselineKwh)
        {
            return null;
        }

        // No MainMeter yet means no Meter Reading has ever been logged for this Household — short
        // circuit without the read-only lookup's caller having to also query MeterReadings.
        var mainMeter = await readingRepository.FindMainMeterByHouseholdAsync(householdId, cancellationToken);
        if (mainMeter is null)
        {
            return null;
        }

        // Fetched before the reading fetch so its PreviousMeterReadingId (if any) can be passed as
        // the bounded fetch's must-include anchor below — an open prompt can stay unresolved
        // indefinitely while readings keep arriving (AD-12), so the fetch must be able to widen to
        // cover it regardless of how stale it's gotten.
        var openPrompt = await regressionPromptRepository.GetOpenForHouseholdAsync(householdId, cancellationToken);

        // AD-14: only ever MeterReading data is read/summed here — no other reading/log type is
        // referenced anywhere in this method (see the guard test in EnergyTracker.Architecture.Tests).
        //
        // Anchored on PreviousMeterReadingId, not MeterReadingId (the prompt's own trigger) —
        // ExcludeFromOpenPrompt below returns everything strictly before the trigger, so the last
        // reading ComputePaceToDate actually windows from is the one immediately preceding it.
        // PreviousMeterReadingId's timestamp is always <= the trigger's own timestamp, so
        // anchoring there also keeps the trigger itself in the fetched set (ExcludeFromOpenPrompt
        // would otherwise throw for a trigger that got excluded from the fetch entirely).
        var recentReadings = await readingRepository.GetRecentByMainMeterAsync(
            mainMeter.Id, RecentReadingWindowDays, openPrompt?.PreviousMeterReadingId, cancellationToken, asOfUtc);
        var includedReadings = PatternDetectiveCalculator.ExcludeFromOpenPrompt(recentReadings, openPrompt?.MeterReadingId);

        var resolvedPrompts = await regressionPromptRepository.GetResolvedForMainMeterAsync(mainMeter.Id, cancellationToken);
        var resolvedPromptsByTriggeringReadingId = resolvedPrompts.ToDictionary(p => p.MeterReadingId);

        var paceResult = PatternDetectiveCalculator.ComputePaceToDate(includedReadings, resolvedPromptsByTriggeringReadingId);
        if (paceResult is null)
        {
            return null;
        }

        var baselineToDateKwh = BonusDecayNormalizer.NormalizeToDate(yearlyBaselineKwh, bonusTermsKwh: 0m, paceResult.Value.Elapsed);
        var status = PatternDetectiveCalculator.ResolveStatus(paceResult.Value.PaceToDateKwh, baselineToDateKwh, household.TrendingThresholdKwh);

        // AC #3: "unusually long gap since the last reading" — measured from the most recent
        // *included* reading to now, not a gap between two readings within the walked sequence.
        // Story 4.3: "now" becomes the historical asOfUtc point when supplied, so a forward
        // recompute reproduces what this figure would have looked like at that point in time.
        var now = asOfUtc ?? DateTimeOffset.UtcNow;
        var lastReading = includedReadings[^1];
        var daysSinceLastReading = (now - lastReading.ReadingTimestamp).TotalDays;
        var isLowConfidence = daysSinceLastReading > household.LowConfidenceGapDays;

        // Story 3.3 (AC #1, #2; AD-14): Smart Plug data can only ever soften this flag, never
        // touch PaceToDateKwh/BaselineToDateKwh/the Trending resolution above — the entire
        // "sharpening" mechanism for this story. A Household with zero Smart Plug coverage simply
        // never has this signal flip true->false, so AC #1 needs no special-casing here.
        if (isLowConfidence)
        {
            var hasCorroboratingCoverage = await smartPlugCoverageSignal.HasCoverageDuringAsync(
                householdId, lastReading.ReadingTimestamp, now, cancellationToken);
            if (hasCorroboratingCoverage)
            {
                isLowConfidence = false;
            }
        }

        return new CurrentStatusResult(
            Status: status,
            PaceToDateKwh: paceResult.Value.PaceToDateKwh,
            BaselineToDateKwh: baselineToDateKwh,
            IsLowConfidence: isLowConfidence,
            ElapsedDays: paceResult.Value.Elapsed.TotalDays,
            TrendingThresholdKwh: household.TrendingThresholdKwh,
            DaysSinceLastReading: daysSinceLastReading,
            LowConfidenceGapDaysThreshold: household.LowConfidenceGapDays);
    }
}
