using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class StatusSnapshotRepository(EnergyTrackerDbContext dbContext) : IStatusSnapshotRepository
{
    public async Task<IReadOnlyList<StatusSnapshot>> GetForHouseholdAsync(Guid householdId, CancellationToken cancellationToken) =>
        await dbContext.StatusSnapshots
            .Where(s => s.HouseholdId == householdId)
            .OrderBy(s => s.ComputedAtUtc)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
}
