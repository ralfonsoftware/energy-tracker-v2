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
    IAsyncEnumerable<AuditCorrection> AuditCorrections);
