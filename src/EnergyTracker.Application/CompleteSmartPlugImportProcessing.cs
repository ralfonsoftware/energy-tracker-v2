using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Domain.Calculations;
using Microsoft.Extensions.Logging;

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
    ISmartPlugImportRepository smartPlugImportRepository,
    IStatusRecomputeService statusRecomputeService,
    ILogger<CompleteSmartPlugImportProcessing> logger)
{
    public async Task ExecuteAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        // AD-10: gap detection needs a resolved Power Point (for the cross-import trailing-average
        // lookup) — this helper only ever runs once one is known, so every reading here already
        // carries the same PowerPointId. Enforced explicitly (not just by comment) since both
        // call sites (ProcessSmartPlugImport, MapSmartPlugImportToPowerPoint) already commit the
        // import/readings before reaching here — a violated invariant should fail loudly.
        if (readings.Count == 0)
        {
            throw new ArgumentException("CompleteSmartPlugImportProcessing requires at least one reading.", nameof(readings));
        }

        var powerPointId = readings[0].PowerPointId
            ?? throw new ArgumentException("CompleteSmartPlugImportProcessing requires readings with a resolved PowerPointId.", nameof(readings));

        // A gap-detection/persistence failure must never fail the caller's already-successful
        // write — by the time this runs, ProcessSmartPlugImport/MapSmartPlugImportToPowerPoint have
        // already committed the import as Completed. Swallow and log here, mirroring
        // StatusRecomputeService.RecomputeAsync's own "a recompute failure must never fail the
        // caller's already-successful write" discipline for the exact same reason — otherwise this
        // failure propagates to the caller's catch block, which (for ProcessSmartPlugImport) tries
        // to re-AddAsync an already-tracked SmartPlugImport with the same Id and crashes on a
        // duplicate-key error, or (for MapSmartPlugImportToPowerPoint, which has no catch at all)
        // surfaces as a 500 for a mapping that already succeeded.
        try
        {
            // Bound the prior-readings query to the trailing window SmartPlugGapDetector can ever
            // actually read (rather than a Power Point's full history) — the "how long has this
            // Power Point had ANY history" question is answered separately below via a cheap
            // indexed MIN lookup, not by scanning this windowed list.
            var rangeStart = readings.Min(r => DateOnly.FromDateTime(r.IntervalStart.DateTime));
            var sinceDate = rangeStart.AddDays(-SmartPlugGapDetector.TrailingAverageWindowDays);
            var priorReadings = await smartPlugImportRepository.ListPriorReadingsByPowerPointAsync(powerPointId, import.Id, sinceDate, cancellationToken);
            var firstEverReadingDate = await smartPlugImportRepository.FindFirstReadingDateByPowerPointAsync(powerPointId, cancellationToken);
            var gaps = SmartPlugGapDetector.DetectGaps(import.HouseholdId, import.Id, powerPointId, readings, priorReadings, firstEverReadingDate, DateTimeOffset.UtcNow);
            if (gaps.Count > 0)
            {
                await smartPlugImportRepository.AddGapsAsync(gaps, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Gap detection failed for SmartPlugImport {SmartPlugImportId}; the import itself already succeeded.", import.Id);
        }

        await statusRecomputeService.RecomputeAsync(import.HouseholdId, cancellationToken);
    }
}
