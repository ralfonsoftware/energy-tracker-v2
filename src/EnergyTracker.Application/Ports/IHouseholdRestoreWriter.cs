using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

// Household's own row is the one exception to "delete then insert" (Task 3) — every field here
// mirrors HouseholdSettingsExportDto except Id/CreatedAtUtc (never touched) and the two
// AiPlausibilityBackend* fields (AD-19: deployment-level, read-only in the export, never written
// back).
public record HouseholdSettingsPatch(
    string Locale,
    string Currency,
    decimal? YearlyBaselineKwh,
    decimal TrendingThresholdKwh,
    int LowConfidenceGapDays,
    int TariffCheckCadenceMonths,
    bool AiPlausibilityEnabled);

// Bundles every reconstructed Domain entity for one restore. RestoreHouseholdData (Application)
// has already set HouseholdId explicitly on every entity and mapped every DTO enum-string field
// (Classification/Status) back to its real enum by the time this reaches the writer — the writer's
// only job is the chunked delete-then-insert mechanics (AD-2/AD-6), never entity construction.
public record HouseholdRestoreData(
    Guid HouseholdId,
    HouseholdSettingsPatch HouseholdSettings,
    IReadOnlyList<HouseholdMember> HouseholdMembers,
    MainMeter? MainMeter,
    IReadOnlyList<Room> Rooms,
    IReadOnlyList<PowerPoint> PowerPoints,
    IReadOnlyList<Device> Devices,
    IReadOnlyList<MeterReading> MeterReadings,
    IReadOnlyList<MeterRegressionPrompt> MeterRegressionPrompts,
    IReadOnlyList<Tariff> Tariffs,
    IReadOnlyList<Event> Events,
    IReadOnlyList<SmartPlugReading> SmartPlugReadings,
    IReadOnlyList<StatusSnapshot> StatusSnapshots,
    IReadOnlyList<AuditCorrection> AuditCorrections);

// One port, one adapter (HouseholdRestoreWriter) — mirrors IHouseholdExportReader/
// HouseholdExportReader's placement (Story 7.1), the closest sibling: a bulk cross-entity
// operation over the whole Household that only makes sense against a real EF Core DbContext, kept
// out of Application per AD-1.
public interface IHouseholdRestoreWriter
{
    Task RestoreAsync(HouseholdRestoreData data, CancellationToken cancellationToken);
}
