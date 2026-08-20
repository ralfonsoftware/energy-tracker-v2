using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class BackgroundJobRepository(EnergyTrackerDbContext dbContext) : IBackgroundJobRepository
{
    public Task<BackgroundJob?> FindByIdAsync(Guid householdId, Guid jobId, CancellationToken cancellationToken) =>
        dbContext.BackgroundJobs.SingleOrDefaultAsync(j => j.HouseholdId == householdId && j.Id == jobId, cancellationToken);
}
