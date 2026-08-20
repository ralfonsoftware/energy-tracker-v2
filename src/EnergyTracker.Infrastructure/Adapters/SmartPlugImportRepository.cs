using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

public class SmartPlugImportRepository(EnergyTrackerDbContext dbContext) : ISmartPlugImportRepository
{
    public async Task AddAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);
        if (readings.Count > 0)
        {
            await dbContext.SmartPlugReadings.AddRangeAsync(readings, cancellationToken);
        }

        // Single SaveChangesAsync — one transaction, so a partially persisted import (row
        // without its readings) is never observable by a later read (Story 3.2/3.3).
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<SmartPlugImport?> FindByBackgroundJobIdAsync(Guid backgroundJobId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.BackgroundJobId == backgroundJobId, cancellationToken);

    public Task<SmartPlugImport?> FindByIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == smartPlugImportId, cancellationToken);

    public async Task<IReadOnlyList<SmartPlugReading>> ListReadingsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .ToListAsync(cancellationToken);

    public async Task UpdateMappingAsync(SmartPlugImport import, IReadOnlyList<SmartPlugReading> readings, CancellationToken cancellationToken)
    {
        // import/readings are already tracked by this same scoped DbContext — they came from
        // FindByIdAsync/ListReadingsByImportIdAsync earlier in the same request. Calling
        // Update()/UpdateRange() here would mark every property Modified (forcing a full-column
        // UPDATE) instead of letting the change tracker diff only what ExecuteAsync actually
        // touched — SaveChangesAsync alone is enough.
        // Single SaveChangesAsync — one transaction, so a partially updated import/readings set
        // is never observable by a later read.
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
