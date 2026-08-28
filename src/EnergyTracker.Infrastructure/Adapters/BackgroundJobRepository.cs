using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class BackgroundJobRepository(EnergyTrackerDbContext dbContext) : IBackgroundJobRepository
{
    public Task<BackgroundJob?> FindByIdAsync(Guid householdId, Guid jobId, CancellationToken cancellationToken) =>
        dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.HouseholdId == householdId && j.Id == jobId, cancellationToken);

    public async Task<IReadOnlyList<BackgroundJob>> ListByJobTypeAsync(Guid householdId, string jobType, CancellationToken cancellationToken) =>
        await dbContext.BackgroundJobs
            .Where(j => j.HouseholdId == householdId && j.JobType == jobType)
            .OrderByDescending(j => j.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HouseholdMember>> FindMembersByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken) =>
        await dbContext.HouseholdMembers
            .AsNoTracking()
            .Where(m => memberIds.Contains(m.Id))
            .ToListAsync(cancellationToken);
}
