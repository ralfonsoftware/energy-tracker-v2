using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

public class StatusRecomputeService(
    GetCurrentStatus getCurrentStatus,
    EnergyTrackerDbContext dbContext,
    IHouseholdRecomputeLock recomputeLock,
    ILogger<StatusRecomputeService> logger) : IStatusRecomputeService
{
    public async Task RecomputeAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // Serializes concurrent recomputes for the same Household (meter reading save, Smart Plug
        // import direct-match completion, and import mapping completion can all trigger this
        // nearly simultaneously) so the read-then-write body below never races and leaves the
        // most recent StatusSnapshot row not reflecting the newest committed data. Acquisition
        // itself lives inside the same catch-and-log boundary as the body below, using the
        // identical cancellation-exclusion filter, so a benign cancellation during either phase is
        // never logged as an error.
        IAsyncDisposable recomputeLockHandle;
        try
        {
            recomputeLockHandle = await recomputeLock.AcquireAsync(householdId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A recompute failure must never fail the caller's already-successful write (e.g.
            // CreateMeterReading's MeterReading is committed before this runs) — same reasoning as
            // the body's own catch below, just covering lock acquisition (e.g. a TimeoutException)
            // too.
            logger.LogError(ex, "Failed to acquire the recompute lock for Household {HouseholdId}; the triggering write already succeeded.", householdId);
            return;
        }

        await using (recomputeLockHandle)
        {
            // A recompute failure must never fail the caller's already-successful write (e.g.
            // CreateMeterReading's MeterReading is committed before this runs) — swallow here and log
            // instead of letting the exception surface as a 500 for a request that actually
            // succeeded. The StatusSnapshot history is left with a gap for this write, but the next
            // successful recompute (or a live GET /api/status read) recovers correctness.
            try
            {
                var result = await getCurrentStatus.ExecuteAsync(householdId, cancellationToken);
                if (result is null)
                {
                    // Undefined (fewer than two Readings, or no Yearly Baseline) — nothing meaningful
                    // to persist. AC #8 only requires a write "when Status is (re)computed"; this
                    // story's reading of that is a definite Status only (confirmed with Ralf during
                    // dev-story activation — see Completion Notes).
                    return;
                }

                // Story 4.3: captured once and used for both fields — for this call site (the live,
                // "now" recompute) EffectiveAtUtc and ComputedAtUtc are always the same instant.
                var nowUtc = DateTimeOffset.UtcNow;

                await dbContext.StatusSnapshots.AddAsync(
                    new StatusSnapshot
                    {
                        Id = Guid.NewGuid(),
                        HouseholdId = householdId,
                        Status = result.Status,
                        PaceToDateKwh = result.PaceToDateKwh,
                        BaselineToDateKwh = result.BaselineToDateKwh,
                        IsLowConfidence = result.IsLowConfidence,
                        ComputedAtUtc = nowUtc,
                        EffectiveAtUtc = nowUtc,
                    },
                    cancellationToken);

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Status recompute failed for Household {HouseholdId}; the triggering write already succeeded.", householdId);
            }
        }
    }

    // Story 4.3, AC #3: recomputes every existing StatusSnapshot point at/after fromEffectiveAtUtc
    // using the (now-corrected) current reading data, and appends a superseding row for each —
    // StatusSnapshot stays immutable/insert-only; the read side (StatusSnapshotRepository) is what
    // makes the fresh row "win" over the stale one at the same EffectiveAtUtc.
    public async Task RecomputeForwardFromAsync(Guid householdId, DateTimeOffset fromEffectiveAtUtc, CancellationToken cancellationToken)
    {
        IAsyncDisposable recomputeLockHandle;
        try
        {
            recomputeLockHandle = await recomputeLock.AcquireAsync(householdId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A recompute failure must never fail the caller's already-successful edit — mirrors
            // RecomputeAsync's identical reasoning for lock-acquisition failures.
            logger.LogError(ex, "Failed to acquire the recompute lock for Household {HouseholdId} during a forward recompute; the triggering edit already succeeded.", householdId);
            return;
        }

        await using (recomputeLockHandle)
        {
            try
            {
                var affectedEffectiveAtPoints = await dbContext.StatusSnapshots
                    .Where(s => s.HouseholdId == householdId && s.EffectiveAtUtc >= fromEffectiveAtUtc)
                    .Select(s => s.EffectiveAtUtc)
                    .Distinct()
                    .OrderBy(t => t)
                    .ToListAsync(cancellationToken);

                foreach (var effectiveAt in affectedEffectiveAtPoints)
                {
                    var result = await getCurrentStatus.ExecuteAsync(householdId, cancellationToken, asOfUtc: effectiveAt);
                    if (result is null)
                    {
                        // Structurally unreachable through this call path today — editing a Meter
                        // Reading's value never removes a reading or unsets the Yearly Baseline, so
                        // a point that was previously definite can't become undefined here. Skip
                        // defensively rather than throw, matching the codebase's existing posture
                        // for this class of gap (see deferred-work.md).
                        continue;
                    }

                    await dbContext.StatusSnapshots.AddAsync(
                        new StatusSnapshot
                        {
                            Id = Guid.NewGuid(),
                            HouseholdId = householdId,
                            Status = result.Status,
                            PaceToDateKwh = result.PaceToDateKwh,
                            BaselineToDateKwh = result.BaselineToDateKwh,
                            IsLowConfidence = result.IsLowConfidence,
                            ComputedAtUtc = DateTimeOffset.UtcNow,
                            EffectiveAtUtc = effectiveAt,
                        },
                        cancellationToken);
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Forward Status recompute failed for Household {HouseholdId}; the triggering edit already succeeded.", householdId);
            }
        }
    }
}
