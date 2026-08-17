using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

public class StatusRecomputeService(
    GetCurrentStatus getCurrentStatus,
    EnergyTrackerDbContext dbContext,
    ILogger<StatusRecomputeService> logger) : IStatusRecomputeService
{
    public async Task RecomputeAsync(Guid householdId, CancellationToken cancellationToken)
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
