using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IStatusSnapshotRepository
{
    // Story 4.3: one row per distinct EffectiveAtUtc — when a correction has superseded a
    // historical point, only the row with the greatest ComputedAtUtc for that EffectiveAtUtc is
    // returned (the "latest write wins" read-time tiebreak that lets a correction supersede a
    // point without ever mutating a row). Ascending by EffectiveAtUtc (chart reads
    // chronologically), then Id as the deterministic tiebreak on an identical timestamp — same
    // tiebreak discipline as FindImmediatelyPrecedingAsync/GetPageForMainMeterAsync elsewhere in
    // this codebase.
    Task<IReadOnlyList<StatusSnapshot>> GetForHouseholdAsync(Guid householdId, CancellationToken cancellationToken);
}
