using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface IBackgroundJobRepository
{
    Task<BackgroundJob?> FindByIdAsync(Guid householdId, Guid jobId, CancellationToken cancellationToken);
}
