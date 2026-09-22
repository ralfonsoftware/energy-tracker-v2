using EnergyTracker.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

// Read-only bulk fetch across every in-scope entity type (Story 7.1). AsNoTracking throughout —
// mirrors EventRepository/BackgroundJobRepository/SmartPlugImportRepository's own read-only query
// convention. Every entity carries an explicit HouseholdId match here; for entities that already
// have the DbContext's AD-3 global query filter, this is redundant-but-harmless (matches this
// codebase's existing adapter convention, e.g. TariffRepository/StatusSnapshotRepository). For
// HouseholdMember, which carries no query filter, the explicit match is load-bearing — same as
// HouseholdRepository's own Household/HouseholdMember reads. Never IgnoreQueryFilters/FromSqlRaw/
// Find. Plain LINQ over the AD-2 portable relational subset only, so this reads identically against
// both providers; no migration needed.
public class HouseholdExportReader(EnergyTrackerDbContext dbContext) : IHouseholdExportReader
{
    public async Task<HouseholdExportData> GetExportDataAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // Household itself carries no AD-3 filter (it's the tenant root, fetched by id directly —
        // same as HouseholdRepository.FindByIdAsync).
        var household = await dbContext.Households.AsNoTracking()
            .SingleAsync(h => h.Id == householdId, cancellationToken);

        var householdMembers = await dbContext.HouseholdMembers.AsNoTracking()
            .Where(m => m.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var mainMeter = await dbContext.MainMeters.AsNoTracking()
            .Where(m => m.HouseholdId == householdId)
            .SingleOrDefaultAsync(cancellationToken);

        var meterReadings = await dbContext.MeterReadings.AsNoTracking()
            .Where(r => r.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var meterRegressionPrompts = await dbContext.MeterRegressionPrompts.AsNoTracking()
            .Where(p => p.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var tariffs = await dbContext.Tariffs.AsNoTracking()
            .Where(t => t.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var events = await dbContext.Events.AsNoTracking()
            .Where(e => e.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        // Archived rows included deliberately (AD-10 history integrity) — a restore that dropped
        // them would corrupt the by-value snapshots SmartPlugReading/Event already carry.
        var rooms = await dbContext.Rooms.AsNoTracking()
            .Where(r => r.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var powerPoints = await dbContext.PowerPoints.AsNoTracking()
            .Where(p => p.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var devices = await dbContext.Devices.AsNoTracking()
            .Where(d => d.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var smartPlugReadings = await dbContext.SmartPlugReadings.AsNoTracking()
            .Where(r => r.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var statusSnapshots = await dbContext.StatusSnapshots.AsNoTracking()
            .Where(s => s.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        var auditCorrections = await dbContext.AuditCorrections.AsNoTracking()
            .Where(c => c.HouseholdId == householdId)
            .ToListAsync(cancellationToken);

        return new HouseholdExportData(
            household,
            householdMembers,
            mainMeter,
            meterReadings,
            meterRegressionPrompts,
            tariffs,
            events,
            rooms,
            powerPoints,
            devices,
            smartPlugReadings,
            statusSnapshots,
            auditCorrections);
    }
}
