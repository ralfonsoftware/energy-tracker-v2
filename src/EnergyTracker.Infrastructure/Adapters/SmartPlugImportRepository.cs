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
}
