using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

// New port rather than a bulk-read method bolted onto each existing repository (Story 7.1 Dev
// Notes "Read-path design decision") — keeps the narrow, purpose-built existing repository
// interfaces (ITariffRepository, IEventRepository, etc.) uncluttered with a bulk-export method
// none of their other callers need. The single adapter reads EnergyTrackerDbContext's DbSets
// directly, all through the AD-3 global query filter (or an explicit HouseholdId filter for
// HouseholdMember, which is a documented AD-3 exception) — never IgnoreQueryFilters/FromSqlRaw/Find.
public interface IHouseholdExportReader
{
    Task<HouseholdExportData> GetExportDataAsync(Guid householdId, CancellationToken cancellationToken);
}

// One raw domain-entity bag per Household, scoped entirely by the AD-3 query filter (or an
// explicit HouseholdId match for HouseholdMember). Deliberately excludes HouseholdInvite (a live
// bearer credential, not data) and BackgroundJob/SmartPlugImport/SmartPlugImportGap (transient
// job-queue metadata with its own 30-day lifecycle) — see docs/data-export-format.md's "Entity
// scope" section for the full disclosed rationale.
//
// Household/MainMeter stay single, eagerly-fetched values (tiny, always needed up front — the
// filename and format-version metadata depend on neither being deferred). Every other collection
// is an IAsyncEnumerable<T>, keyset-paginated by the adapter (spec-household-export-oom-fix.md)
// so a large household's export never materializes a whole collection in memory at once.
//
// Contract for every IAsyncEnumerable<T> field here: enumerate each one exactly once, and never two
// of them concurrently — they're backed by paged queries against a single scoped DbContext, which
// EF Core does not support running more than one operation on at a time. Re-enumerating one re-runs
// its paged query from scratch rather than replaying prior results. HouseholdExportEndpoints'
// sequential `await foreach` per collection (never two enumerated at once) is the only intended
// consumption pattern.
public record HouseholdExportData(
    Household Household,
    IAsyncEnumerable<HouseholdMember> HouseholdMembers,
    MainMeter? MainMeter,
    IAsyncEnumerable<MeterReading> MeterReadings,
    IAsyncEnumerable<MeterRegressionPrompt> MeterRegressionPrompts,
    IAsyncEnumerable<Tariff> Tariffs,
    IAsyncEnumerable<Event> Events,
    IAsyncEnumerable<Room> Rooms,
    IAsyncEnumerable<PowerPoint> PowerPoints,
    IAsyncEnumerable<Device> Devices,
    IAsyncEnumerable<SmartPlugReading> SmartPlugReadings,
    IAsyncEnumerable<StatusSnapshot> StatusSnapshots,
    IAsyncEnumerable<AuditCorrection> AuditCorrections,
    HouseholdExportStats Stats);

// Success-path observability (spec-household-export-observability.md, Epic 7 retro action item
// #1): the only source of truth for page round trips is HouseholdExportReader.PageAsync's own
// loop, so this accumulator is populated there and simply carried by HouseholdExportData/
// HouseholdExportStream to wherever it's eventually logged (HouseholdExportEndpoints) — no second
// counting mechanism, no port-signature change. Pre-seeded with every collection name at
// construction so the map always has a stable, complete shape even for a collection whose query
// never runs at all (e.g. MeterReadings when the Household has no MainMeter yet); RecordPage
// indexes into that pre-seeded dictionary (throws on an unregistered name) rather than upserting,
// which also catches a typo'd collection name instead of silently dropping it from the map. Not
// thread-safe by design — matches the port's own documented single-threaded, one-collection-at-a-
// time enumeration contract above.
public sealed class HouseholdExportStats
{
    private readonly Dictionary<string, (int RowCount, int PageCount)> _byCollection;

    public HouseholdExportStats(IEnumerable<string> collectionNames)
    {
        _byCollection = collectionNames.ToDictionary(name => name, _ => (RowCount: 0, PageCount: 0));
    }

    // Called once per DB round trip, including a page that comes back empty/short — an empty page
    // still cost a query, so it's one page round trip against zero rows, not zero round trips.
    public void RecordPage(string collectionName, int rowCount)
    {
        var current = _byCollection[collectionName];
        _byCollection[collectionName] = (current.RowCount + rowCount, current.PageCount + 1);
    }

    public IReadOnlyDictionary<string, (int RowCount, int PageCount)> Snapshot() => _byCollection;
}
