using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
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
}
