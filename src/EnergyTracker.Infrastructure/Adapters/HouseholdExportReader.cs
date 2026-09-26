using System.Runtime.CompilerServices;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

// Read-only bulk fetch across every in-scope entity type (Story 7.1; streamed/paged per
// spec-household-export-oom-fix.md to fix the production OOM at HouseholdExportEndpoints.cs).
// AsNoTracking throughout — mirrors EventRepository/BackgroundJobRepository/SmartPlugImportRepository's
// own read-only query convention. Every entity carries an explicit HouseholdId match here (except
// MeterReadings, see below); for entities that already have the DbContext's AD-3 global query
// filter, this is redundant-but-harmless (matches this codebase's existing adapter convention,
// e.g. TariffRepository/StatusSnapshotRepository). For HouseholdMember, which carries no query
// filter, the explicit match is load-bearing — same as HouseholdRepository's own Household/
// HouseholdMember reads. Never IgnoreQueryFilters/FromSqlRaw/Find. Plain LINQ over the AD-2
// portable relational subset only, so this reads identically against both providers.
//
// Every collection beyond Household/MainMeter is keyset-paginated: PageAsync repeatedly runs a
// small Take(PageSize) query ordered by (cursor column, Id), advancing past the last row's cursor
// each round trip, so peak memory for any one collection never exceeds one page regardless of a
// household's total data volume. Never offset/Skip-based — that would re-scan and re-sort already-
// returned rows on every page at this data volume (spec Design Notes).
public class HouseholdExportReader(EnergyTrackerDbContext dbContext) : IHouseholdExportReader
{
    // One paged round trip fetches at most this many rows per collection. Large enough that
    // pagination overhead doesn't dominate for a big household's SmartPlugReadings, small enough
    // that one page's worth of rows plus its DTO projection stays a small fraction of the
    // ~300-400Mi peak-working-set target even for the widest entity in this set.
    private const int PageSize = 500;

    public async Task<HouseholdExportData> GetExportDataAsync(Guid householdId, CancellationToken cancellationToken)
    {
        // Household itself carries no AD-3 filter (it's the tenant root, fetched by id directly —
        // same as HouseholdRepository.FindByIdAsync).
        var household = await dbContext.Households.AsNoTracking()
            .SingleAsync(h => h.Id == householdId, cancellationToken);

        var mainMeter = await dbContext.MainMeters.AsNoTracking()
            .Where(m => m.HouseholdId == householdId)
            .SingleOrDefaultAsync(cancellationToken);

        // Names match HouseholdExportEndpoints.WriteArrayAsync's own JSON property-name literals
        // exactly (spec-household-export-observability.md) — the success log's stats map keys
        // line up 1:1 with the wire property a reader would look at.
        var stats = new HouseholdExportStats([
            "householdMembers", "meterReadings", "meterRegressionPrompts", "tariffs", "events",
            "rooms", "powerPoints", "devices", "smartPlugReadings", "statusSnapshots", "auditCorrections",
        ]);

        return new HouseholdExportData(
            household,
            GetHouseholdMembersAsync(householdId, stats, cancellationToken),
            mainMeter,
            GetMeterReadingsAsync(mainMeter, stats, cancellationToken),
            GetMeterRegressionPromptsAsync(householdId, stats, cancellationToken),
            GetTariffsAsync(householdId, stats, cancellationToken),
            GetEventsAsync(householdId, stats, cancellationToken),
            GetRoomsAsync(householdId, stats, cancellationToken),
            GetPowerPointsAsync(householdId, stats, cancellationToken),
            GetDevicesAsync(householdId, stats, cancellationToken),
            GetSmartPlugReadingsAsync(householdId, stats, cancellationToken),
            GetStatusSnapshotsAsync(householdId, stats, cancellationToken),
            GetAuditCorrectionsAsync(householdId, stats, cancellationToken),
            stats);
    }

    // Shared keyset-pagination loop: repeatedly asks `pageQuery` for the next page after the
    // current (cursor, Id) position, yields every row in it, then advances past the last row
    // returned. Stops as soon as a page comes back short of PageSize (the only way to know
    // there's no next page without an extra round trip). Entity-specific ordering/filtering stays
    // in each entity's own `pageQuery` delegate below — this only owns the loop/cursor mechanics.
    // Records one HouseholdExportStats page every round trip (spec-household-export-observability.md)
    // — including a short/empty page, since the query itself still ran.
    private static async IAsyncEnumerable<T> PageAsync<T, TCursor>(
        Func<(TCursor Cursor, Guid Id)?, IQueryable<T>> pageQuery,
        Func<T, TCursor> cursorSelector,
        Func<T, Guid> idSelector,
        string collectionName,
        HouseholdExportStats stats,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        (TCursor Cursor, Guid Id)? cursor = null;

        while (true)
        {
            var page = await pageQuery(cursor).ToListAsync(cancellationToken);
            stats.RecordPage(collectionName, page.Count);
            if (page.Count == 0)
            {
                yield break;
            }

            foreach (var item in page)
            {
                yield return item;
            }

            var last = page[^1];
            cursor = (cursorSelector(last), idSelector(last));

            if (page.Count < PageSize)
            {
                yield break;
            }
        }
    }

    private IAsyncEnumerable<HouseholdMember> GetHouseholdMembersAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<HouseholdMember, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.HouseholdMembers.AsNoTracking().Where(m => m.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(m => m.CreatedAtUtc > c.Cursor || (m.CreatedAtUtc == c.Cursor && m.Id > c.Id));
                }

                return query.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).Take(PageSize);
            },
            m => m.CreatedAtUtc,
            m => m.Id,
            "householdMembers",
            stats,
            cancellationToken);

    // Pages by MainMeterId, not HouseholdId — reuses the existing (MainMeterId, ReadingTimestamp)
    // index (spec Design Notes) rather than needing a new composite index. The AD-3 global query
    // filter on MeterReading still applies automatically underneath this filter, so tenant
    // isolation holds regardless. No MainMeter (a Household that hasn't set one up yet) means no
    // MeterReading can exist for it via the FK — yield nothing rather than querying (stats stay at
    // their pre-seeded zero for "meterReadings" in that case, spec-household-export-observability.md).
    private async IAsyncEnumerable<MeterReading> GetMeterReadingsAsync(
        MainMeter? mainMeter,
        HouseholdExportStats stats,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (mainMeter is null)
        {
            yield break;
        }

        await foreach (var reading in PageAsync<MeterReading, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.MeterReadings.AsNoTracking().Where(r => r.MainMeterId == mainMeter.Id);
                if (cursor is { } c)
                {
                    query = query.Where(r => r.ReadingTimestamp > c.Cursor || (r.ReadingTimestamp == c.Cursor && r.Id > c.Id));
                }

                return query.OrderBy(r => r.ReadingTimestamp).ThenBy(r => r.Id).Take(PageSize);
            },
            r => r.ReadingTimestamp,
            r => r.Id,
            "meterReadings",
            stats,
            cancellationToken))
        {
            yield return reading;
        }
    }

    private IAsyncEnumerable<MeterRegressionPrompt> GetMeterRegressionPromptsAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<MeterRegressionPrompt, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.MeterRegressionPrompts.AsNoTracking().Where(p => p.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(p => p.CreatedAtUtc > c.Cursor || (p.CreatedAtUtc == c.Cursor && p.Id > c.Id));
                }

                return query.OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).Take(PageSize);
            },
            p => p.CreatedAtUtc,
            p => p.Id,
            "meterRegressionPrompts",
            stats,
            cancellationToken);

    private IAsyncEnumerable<Tariff> GetTariffsAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<Tariff, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.Tariffs.AsNoTracking().Where(t => t.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(t => t.CreatedAtUtc > c.Cursor || (t.CreatedAtUtc == c.Cursor && t.Id > c.Id));
                }

                return query.OrderBy(t => t.CreatedAtUtc).ThenBy(t => t.Id).Take(PageSize);
            },
            t => t.CreatedAtUtc,
            t => t.Id,
            "tariffs",
            stats,
            cancellationToken);

    // Cursor is the existing (HouseholdId, OccurredAt, CreatedAtUtc) index's own trailing columns
    // — no new index needed (spec Design Notes). Id is only an in-memory final tiebreaker for the
    // vanishingly rare case two Events share both OccurredAt and CreatedAtUtc; it isn't part of
    // the index.
    private IAsyncEnumerable<Event> GetEventsAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<Event, (DateTimeOffset OccurredAt, DateTimeOffset CreatedAtUtc)>(
            cursor =>
            {
                var query = dbContext.Events.AsNoTracking().Where(e => e.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(e =>
                        e.OccurredAt > c.Cursor.OccurredAt ||
                        (e.OccurredAt == c.Cursor.OccurredAt && e.CreatedAtUtc > c.Cursor.CreatedAtUtc) ||
                        (e.OccurredAt == c.Cursor.OccurredAt && e.CreatedAtUtc == c.Cursor.CreatedAtUtc && e.Id > c.Id));
                }

                return query.OrderBy(e => e.OccurredAt).ThenBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).Take(PageSize);
            },
            e => (e.OccurredAt, e.CreatedAtUtc),
            e => e.Id,
            "events",
            stats,
            cancellationToken);

    // Archived rows included deliberately (AD-10 history integrity) — a restore that dropped
    // them would corrupt the by-value snapshots SmartPlugReading/Event already carry.
    private IAsyncEnumerable<Room> GetRoomsAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<Room, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.Rooms.AsNoTracking().Where(r => r.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(r => r.CreatedAtUtc > c.Cursor || (r.CreatedAtUtc == c.Cursor && r.Id > c.Id));
                }

                return query.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).Take(PageSize);
            },
            r => r.CreatedAtUtc,
            r => r.Id,
            "rooms",
            stats,
            cancellationToken);

    private IAsyncEnumerable<PowerPoint> GetPowerPointsAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<PowerPoint, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.PowerPoints.AsNoTracking().Where(p => p.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(p => p.CreatedAtUtc > c.Cursor || (p.CreatedAtUtc == c.Cursor && p.Id > c.Id));
                }

                return query.OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id).Take(PageSize);
            },
            p => p.CreatedAtUtc,
            p => p.Id,
            "powerPoints",
            stats,
            cancellationToken);

    private IAsyncEnumerable<Device> GetDevicesAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<Device, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.Devices.AsNoTracking().Where(d => d.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(d => d.CreatedAtUtc > c.Cursor || (d.CreatedAtUtc == c.Cursor && d.Id > c.Id));
                }

                return query.OrderBy(d => d.CreatedAtUtc).ThenBy(d => d.Id).Take(PageSize);
            },
            d => d.CreatedAtUtc,
            d => d.Id,
            "devices",
            stats,
            cancellationToken);

    // Cursor is (HouseholdId, IntervalStart, Id) — backed by the new composite index added in
    // SmartPlugReadingConfiguration (unlike the other paged entities, this one needed a new
    // index; spec Design Notes). Id is load-bearing here, not just a formality: two readings can
    // legitimately share IntervalStart (I/O matrix "Page-boundary duplicates").
    private IAsyncEnumerable<SmartPlugReading> GetSmartPlugReadingsAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<SmartPlugReading, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.SmartPlugReadings.AsNoTracking().Where(r => r.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(r => r.IntervalStart > c.Cursor || (r.IntervalStart == c.Cursor && r.Id > c.Id));
                }

                return query.OrderBy(r => r.IntervalStart).ThenBy(r => r.Id).Take(PageSize);
            },
            r => r.IntervalStart,
            r => r.Id,
            "smartPlugReadings",
            stats,
            cancellationToken);

    private IAsyncEnumerable<StatusSnapshot> GetStatusSnapshotsAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<StatusSnapshot, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.StatusSnapshots.AsNoTracking().Where(s => s.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(s => s.ComputedAtUtc > c.Cursor || (s.ComputedAtUtc == c.Cursor && s.Id > c.Id));
                }

                return query.OrderBy(s => s.ComputedAtUtc).ThenBy(s => s.Id).Take(PageSize);
            },
            s => s.ComputedAtUtc,
            s => s.Id,
            "statusSnapshots",
            stats,
            cancellationToken);

    private IAsyncEnumerable<AuditCorrection> GetAuditCorrectionsAsync(Guid householdId, HouseholdExportStats stats, CancellationToken cancellationToken) =>
        PageAsync<AuditCorrection, DateTimeOffset>(
            cursor =>
            {
                var query = dbContext.AuditCorrections.AsNoTracking().Where(ac => ac.HouseholdId == householdId);
                if (cursor is { } c)
                {
                    query = query.Where(ac => ac.CorrectedAtUtc > c.Cursor || (ac.CorrectedAtUtc == c.Cursor && ac.Id > c.Id));
                }

                return query.OrderBy(ac => ac.CorrectedAtUtc).ThenBy(ac => ac.Id).Take(PageSize);
            },
            ac => ac.CorrectedAtUtc,
            ac => ac.Id,
            "auditCorrections",
            stats,
            cancellationToken);
}
