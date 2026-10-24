using EnergyTracker.Application.Ports;
using EnergyTracker.Domain.Calculations;

namespace EnergyTracker.Application;

/// <summary>
/// One instance heartbeat (AD-24, Story 11.13 AC #2, #6, #7): writes this process's own row, purges rows past
/// the retention window and returns the alive OTHER instances whose overlap with this one exceeds the warn
/// threshold. Evidence only (AD-7): nothing reads the heartbeat except the log line the hosted service writes
/// from the result, and the hosted service, not this class, decides what a failed beat means.
/// </summary>
public class RecordInstanceHeartbeat(IAppInstanceRepository repository, InstanceHeartbeatTimings timings, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<InstanceOverlapObservation>> ExecuteAsync(
        Guid instanceId, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await repository.BeatAsync(instanceId, startedAtUtc, now, cancellationToken);
        await PurgeBestEffortAsync(now - timings.RetainFor, cancellationToken);
        var others = await repository.ListAliveOthersAsync(instanceId, now - timings.AliveAfter, cancellationToken);

        // Strictly greater: exactly the threshold does not warn. A stale row never reaches this point,
        // the repository only lists alive ones.
        return others
            .Select(other => new InstanceOverlapObservation(
                other.Id,
                other.StartedAtUtc,
                InstanceOverlapCalculator.Overlap(startedAtUtc, now, other.StartedAtUtc, other.LastSeenAtUtc)))
            .Where(observation => observation.Overlap > timings.OverlapWarnAfter)
            .ToList();
    }

    // Housekeeping only: a purge that fails (lock wait, a deadlock with another instance's purge) must not
    // cost the overlap check below it. Retention is retried at the next beat; cancellation still propagates.
    private async Task PurgeBestEffortAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        try
        {
            await repository.DeleteOlderThanAsync(cutoffUtc, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}
