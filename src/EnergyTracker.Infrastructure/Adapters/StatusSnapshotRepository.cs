using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class StatusSnapshotRepository(EnergyTrackerDbContext dbContext) : IStatusSnapshotRepository
{
    public async Task<IReadOnlyList<StatusSnapshot>> GetForHouseholdAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // Story 4.3: the dedupe-by-EffectiveAtUtc/latest-ComputedAtUtc-wins logic runs in memory,
        // not as a translated GroupBy query — EF Core 10 fails to translate
        // GroupBy(...).Select(g => g.OrderBy...First()) against both Postgres and SQL Server
        // (KeyNotFoundException: "EmptyProjectionMember", confirmed against real databases, not a
        // provider-specific quirk). The DB-side query stays a plain ordered fetch (translates
        // fine); the household's full StatusSnapshot lifetime is already read unbounded elsewhere
        // in this codebase (GetStatusHistory), so this is the same accepted "not yet a measured
        // problem at current data volumes" tradeoff, not a new one.
        var snapshots = await dbContext.StatusSnapshots
            .Where(s => s.HouseholdId == householdId)
            .OrderByDescending(s => s.ComputedAtUtc)
            .ThenByDescending(s => s.Id)
            .ToListAsync(cancellationToken);

        return snapshots
            .GroupBy(s => s.EffectiveAtUtc)
            .Select(g => g.First())
            .OrderBy(s => s.EffectiveAtUtc)
            .ThenBy(s => s.Id)
            .ToList();
    }
}
