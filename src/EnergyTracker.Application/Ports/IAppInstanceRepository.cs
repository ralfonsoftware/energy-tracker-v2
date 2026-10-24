using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

// AD-24: the instance heartbeat table. Instance-level, not Household data (AD-3 exemption), so no
// method takes a householdId.
public interface IAppInstanceRepository
{
    // Moves this instance's LastSeenAtUtc to nowUtc; inserts the row (with startedAtUtc) when none
    // exists, so a row purged by another instance's housekeeping comes back with the real start time.
    Task BeatAsync(Guid instanceId, DateTimeOffset startedAtUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // The rows of other instances last seen at or after aliveSinceUtc.
    Task<IReadOnlyList<AppInstance>> ListAliveOthersAsync(Guid selfId, DateTimeOffset aliveSinceUtc, CancellationToken cancellationToken);

    // Housekeeping: removes rows last seen strictly before cutoffUtc.
    Task DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);

    Task DeleteAsync(Guid instanceId, CancellationToken cancellationToken);
}
