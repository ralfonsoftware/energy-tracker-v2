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
            .AsNoTracking()
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .ToListAsync(cancellationToken);

    public async Task UpdateMappingAsync(
        SmartPlugImport import, Guid powerPointId, string powerPointName, string? roomName, CancellationToken cancellationToken)
    {
        // The default 30s ADO.NET command timeout is tuned for point queries, not a set-based
        // UPDATE across a full import's rows on Basic-tier Azure SQL (5 DTU) — a large Eve Home
        // export (tens of thousands of rows) reliably exceeded it in production ("Execution Timeout
        // Expired" surfaced to the caller as a 500). Raised for the rest of this scoped DbContext's
        // request too, since the readback in MapSmartPlugImportToPowerPoint.ExecuteAsync right
        // after this call reads the same row count under the same DTU ceiling.
        dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(180));

        // One set-based UPDATE server-side — no loading/tracking/diffing hundreds of thousands of
        // rows for a large import (see this method's doc comment on the port interface).
        await dbContext.SmartPlugReadings
            .Where(r => r.SmartPlugImportId == import.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PowerPointId, powerPointId)
                .SetProperty(r => r.PowerPointName, powerPointName)
                .SetProperty(r => r.RoomName, r => roomName ?? r.RoomName),
                cancellationToken);

        // import is already tracked by this same scoped DbContext (loaded via FindByIdAsync
        // earlier in the same request) — only its Status/CompletedAtUtc changed, so
        // SaveChangesAsync alone is enough.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartPlugReading>> ListPriorReadingsByPowerPointAsync(
        Guid powerPointId, Guid excludeSmartPlugImportId, DateOnly sinceDate, CancellationToken cancellationToken)
    {
        // AD-9: SmartPlugReading.IntervalStart is a local-time date encoded with a zero UTC offset
        // — match that encoding here rather than comparing against a real-offset instant.
        var sinceInstant = new DateTimeOffset(sinceDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId
                && r.SmartPlugImportId != excludeSmartPlugImportId
                && r.IntervalStart >= sinceInstant)
            .OrderBy(r => r.IntervalStart)
            .ToListAsync(cancellationToken);
    }

    public async Task<DateOnly?> FindFirstReadingDateByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken)
    {
        var first = await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .OrderBy(r => r.IntervalStart)
            .Select(r => (DateTimeOffset?)r.IntervalStart)
            .FirstOrDefaultAsync(cancellationToken);
        return first is { } value ? DateOnly.FromDateTime(value.DateTime) : null;
    }

    public async Task AddGapsAsync(IReadOnlyList<SmartPlugImportGap> gaps, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImportGaps.AddRangeAsync(gaps, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImportGaps
            .Where(g => g.SmartPlugImportId == smartPlugImportId)
            .OrderBy(g => g.StartDate)
            .ToListAsync(cancellationToken);

    public async Task AddFlaggedForReviewAsync(SmartPlugImport import, SmartPlugImportGap gap, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);
        await dbContext.SmartPlugImportGaps.AddAsync(gap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
