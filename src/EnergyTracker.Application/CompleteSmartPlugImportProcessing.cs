using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;

namespace EnergyTracker.Application;

// AD-7: a SmartPlugImport reaches Completed via two independent code paths —
// ProcessSmartPlugImport's direct-match branch and MapSmartPlugImportToPowerPoint's mapping
// branch (Story 3.2's own Dev Notes flagged this gap for this story's author). Both must run gap
// detection and Status recompute — wiring only one silently breaks sharpening for every household
// that ever needed the create/map prompt. This plain class (not a port — no interface needed, both
// call sites are in-process Application code) exists so that wiring is written once, not twice.
//
// Never call this for the AC #7 "entirely gaps" path (readings.Count == 0) — nothing there was
// used to sharpen anything, and no Power Point was ever resolved (ProcessSmartPlugImport handles
// that case directly via AddFlaggedForReviewAsync).
public class CompleteSmartPlugImportProcessing(
    ISmartPlugImportRepository smartPlugImportRepository, IStatusRecomputeService statusRecomputeService)
{
    public async Task ExecuteAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        // AD-10: gap detection needs a resolved Power Point (for the cross-import trailing-average
        // lookup) — this helper only ever runs once one is known, so every reading here already
        // carries the same PowerPointId.
        var powerPointId = readings[0].PowerPointId!.Value;

        var priorReadings = await smartPlugImportRepository.ListPriorReadingsByPowerPointAsync(powerPointId, import.Id, cancellationToken);
        var gaps = SmartPlugGapDetector.DetectGaps(import.HouseholdId, import.Id, powerPointId, readings, priorReadings, DateTimeOffset.UtcNow);
        if (gaps.Count > 0)
        {
            await smartPlugImportRepository.AddGapsAsync(gaps, cancellationToken);
        }

        await statusRecomputeService.RecomputeAsync(import.HouseholdId, cancellationToken);
    }
}
