using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IStatusSnapshotRepository
{
    // Ascending by ComputedAtUtc (chart reads chronologically), then Id as the deterministic
    // tiebreak on an identical timestamp — same tiebreak discipline as
    // FindImmediatelyPrecedingAsync/GetPageForMainMeterAsync elsewhere in this codebase.
    Task<IReadOnlyList<StatusSnapshot>> GetForHouseholdAsync(Guid householdId, CancellationToken cancellationToken);
}
