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
public record HouseholdExportData(
    Household Household,
    IReadOnlyList<HouseholdMember> HouseholdMembers,
    MainMeter? MainMeter,
    IReadOnlyList<MeterReading> MeterReadings,
    IReadOnlyList<MeterRegressionPrompt> MeterRegressionPrompts,
    IReadOnlyList<Tariff> Tariffs,
    IReadOnlyList<Event> Events,
    IReadOnlyList<Room> Rooms,
    IReadOnlyList<PowerPoint> PowerPoints,
    IReadOnlyList<Device> Devices,
    IReadOnlyList<SmartPlugReading> SmartPlugReadings,
    IReadOnlyList<StatusSnapshot> StatusSnapshots,
    IReadOnlyList<AuditCorrection> AuditCorrections);
