using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-24: the instance heartbeat table. Set-based statements only (ExecuteUpdateAsync/ExecuteDeleteAsync,
// portable LINQ, AD-2). The table has no HouseholdId and no query filter: it is operational data of the
// process, not of a Household (AD-3 exemption).
public class AppInstanceRepository(EnergyTrackerDbContext dbContext) : IAppInstanceRepository
{
    public async Task BeatAsync(Guid instanceId, DateTimeOffset startedAtUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var updated = await dbContext.AppInstances
            .Where(a => a.Id == instanceId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAtUtc, nowUtc), cancellationToken);
        if (updated > 0)
        {
            return;
        }

        // No row yet (first beat) or another instance's housekeeping removed it: insert it with the
        // process's real start time. The id belongs to this process only, so no unique-key race.
        dbContext.AppInstances.Add(new AppInstance { Id = instanceId, StartedAtUtc = startedAtUtc, LastSeenAtUtc = nowUtc });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AppInstance>> ListAliveOthersAsync(Guid selfId, DateTimeOffset aliveSinceUtc, CancellationToken cancellationToken) =>
        await dbContext.AppInstances
            .AsNoTracking()
            .Where(a => a.Id != selfId && a.LastSeenAtUtc >= aliveSinceUtc)
            .ToListAsync(cancellationToken);

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) =>
        await dbContext.AppInstances
            .Where(a => a.LastSeenAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task DeleteAsync(Guid instanceId, CancellationToken cancellationToken) =>
        await dbContext.AppInstances
            .Where(a => a.Id == instanceId)
            .ExecuteDeleteAsync(cancellationToken);
}
