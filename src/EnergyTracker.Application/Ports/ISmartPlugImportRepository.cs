using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

public interface ISmartPlugImportRepository
{
    // Persists the import and all of its parsed readings (if any) as a single unit — a partially
    // persisted import (row without its readings, or vice versa) is never a valid state to
    // observe from Story 3.2/3.3's later reads.
    Task AddAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken);

    // Lets GET /api/jobs/{id} surface the import's own sub-status (e.g. AwaitingPowerPointMapping)
    // alongside the generic BackgroundJob status, since "Completed" alone doesn't tell the client
    // whether the import fully attached to a Power Point.
    Task<SmartPlugImport?> FindByBackgroundJobIdAsync(Guid backgroundJobId, CancellationToken cancellationToken);
}
