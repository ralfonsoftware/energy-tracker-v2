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
    // AD-7: this is the single place the live computation runs — both the GET /api/status read
    // path and IStatusRecomputeService's snapshot-writing path call this same method, so the two
    // can never disagree on exclusion/threshold logic (Task 6's own requirement).
    public async Task<CurrentStatusResult?> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
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

        // AD-14: only ever MeterReading data is read/summed here — no other reading/log type is
        // referenced anywhere in this method (see the guard test in EnergyTracker.Architecture.Tests).
        var allReadings = await readingRepository.GetAllByMainMeterAsync(mainMeter.Id, cancellationToken);
        var openPrompt = await regressionPromptRepository.GetOpenForHouseholdAsync(householdId, cancellationToken);
        var includedReadings = PatternDetectiveCalculator.ExcludeFromOpenPrompt(allReadings, openPrompt?.MeterReadingId);

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
        var now = DateTimeOffset.UtcNow;
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
