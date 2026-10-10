using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class BackgroundJobRepository(EnergyTrackerDbContext dbContext) : IBackgroundJobRepository
{
    // Same value as SmartPlugImportRepository.DeleteBatchSize, so both sweeps are bounded alike.
    internal const int DeleteBatchSize = 200;

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

    public async Task<int> DeleteTerminalByJobTypeAsync(
        Guid householdId, string jobType, DateTimeOffset? completedBeforeUtc, CancellationToken cancellationToken)
    {
        // Ids first (a Take over an ExecuteDelete query is not portable), then delete exactly those
        // — the pattern SmartPlugImportRepository.SweepExpiredAsync uses. Explicit HouseholdId
        // predicate on top of the standard query filter (AD-3); no IgnoreQueryFilters.
        var ids = await dbContext.BackgroundJobs
            .Where(j => j.HouseholdId == householdId
                && j.JobType == jobType
                && (j.Status == BackgroundJobStatus.Completed || j.Status == BackgroundJobStatus.Failed)
                && (completedBeforeUtc == null || j.CompletedAtUtc < completedBeforeUtc))
            .OrderBy(j => j.CompletedAtUtc)
            .ThenBy(j => j.Id)
            .Select(j => j.Id)
            .Take(DeleteBatchSize)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return 0;
        }

        return await dbContext.BackgroundJobs
            .Where(j => j.HouseholdId == householdId && ids.Contains(j.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
