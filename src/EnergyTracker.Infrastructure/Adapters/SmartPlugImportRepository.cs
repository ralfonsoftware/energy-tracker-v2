using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// Partial on purpose: one class per file is bent here because the port is one interface and the tests construct
// this class directly. Import, Mapping and Cleanup hold the parts by responsibility.
public partial class SmartPlugImportRepository(
    EnergyTrackerDbContext dbContext, IAuditCorrectionRecorder auditCorrectionRecorder,
    ILogger<SmartPlugImportRepository> logger) : ISmartPlugImportRepository
{
    // The upsert's chunk size must stay under each provider's per-statement parameter limit for the column count of
    // BuildAwaitingMappingValuesClause: Postgres's hard limit, SQL Server's practical ceiling. ADR 0005.
    private const int PostgresMaxStatementParameters = 65_535;
    private const int SqlServerPracticalMaxStatementParameters = 2_100;
    private const int AwaitingMappingColumnsPerRow = 9;


    public Task<SmartPlugImport?> FindByBackgroundJobIdAsync(Guid backgroundJobId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.BackgroundJobId == backgroundJobId, cancellationToken);

    public Task<SmartPlugImport?> FindByIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        dbContext.SmartPlugImports.SingleOrDefaultAsync(i => i.Id == smartPlugImportId, cancellationToken);

    public async Task<IReadOnlyList<SmartPlugReading>> ListReadingsByImportIdAsync(Guid smartPlugImportId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .AsNoTracking()
            .Where(r => r.SmartPlugImportId == smartPlugImportId)
            .ToListAsync(cancellationToken);

    public async Task<SmartPlugReadingWatermark?> FindLatestReadingWatermarkByPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugReadings
            .Where(r => r.PowerPointId == powerPointId)
            .OrderByDescending(r => r.IntervalStart)
            .Select(r => new SmartPlugReadingWatermark(r.Id, r.IntervalStart, r.KwhValue))
            .FirstOrDefaultAsync(cancellationToken);

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

    public async Task<IReadOnlyList<SmartPlugImportGap>> ListGapsByImportIdsAsync(
        IReadOnlyList<Guid> smartPlugImportIds, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImportGaps
            .AsNoTracking()
            .Where(g => smartPlugImportIds.Contains(g.SmartPlugImportId))
            .OrderBy(g => g.StartDate)
            .ToListAsync(cancellationToken);

    public async Task AddFlaggedForReviewAsync(SmartPlugImport import, SmartPlugImportGap gap, CancellationToken cancellationToken)
    {
        await dbContext.SmartPlugImports.AddAsync(import, cancellationToken);
        await dbContext.SmartPlugImportGaps.AddAsync(gap, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SmartPlugImport>> FindAllByBackgroundJobIdsAsync(
        IReadOnlyList<Guid> backgroundJobIds, CancellationToken cancellationToken) =>
        await dbContext.SmartPlugImports
            .AsNoTracking()
            .Where(i => backgroundJobIds.Contains(i.BackgroundJobId))
            .ToListAsync(cancellationToken);

}
