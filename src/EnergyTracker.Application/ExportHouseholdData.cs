using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

/// <summary>Builds the caller's Household's full "v2" export document — a raw copy of every in-scope entity, zero derivation/recomputation (AC #1, #2, #3, #4; AD-14).</summary>
public class ExportHouseholdData(IHouseholdExportReader exportReader, AiPlausibilityBackendOptions aiPlausibilityBackendOptions)
{
    public const string FormatVersion = "v2";

    public async Task<HouseholdExportResult> ExecuteAsync(Guid householdId, CancellationToken cancellationToken)
    {
        var data = await exportReader.GetExportDataAsync(householdId, cancellationToken);

        return new HouseholdExportResult(
            FormatVersion: FormatVersion,
            ExportedAtUtc: DateTimeOffset.UtcNow,
            Household: ToDto(data.Household),
            HouseholdMembers: data.HouseholdMembers.Select(ToDto).ToList(),
            MainMeter: data.MainMeter is { } mainMeter ? ToDto(mainMeter) : null,
            MeterReadings: data.MeterReadings.Select(ToDto).ToList(),
            MeterRegressionPrompts: data.MeterRegressionPrompts.Select(ToDto).ToList(),
            Tariffs: data.Tariffs.Select(ToDto).ToList(),
            Events: data.Events.Select(ToDto).ToList(),
            Rooms: data.Rooms.Select(ToDto).ToList(),
            PowerPoints: data.PowerPoints.Select(ToDto).ToList(),
            Devices: data.Devices.Select(ToDto).ToList(),
            SmartPlugReadings: data.SmartPlugReadings.Select(ToDto).ToList(),
            StatusSnapshots: data.StatusSnapshots.Select(ToDto).ToList(),
            AuditCorrections: data.AuditCorrections.Select(ToDto).ToList());
    }

    // AiPlausibilityBackendOptions.Model/BaseUrl/ApiKey are deployment secrets (AD-19) — never
    // included. Only Configured/Label, the same non-secret half AC #5 already exposes on
    // GET /api/households/{id}/ai-plausibility.
    private HouseholdSettingsExportDto ToDto(Household household) => new(
        household.Id,
        household.CreatedAtUtc,
        household.Locale,
        household.Currency,
        household.YearlyBaselineKwh,
        household.TrendingThresholdKwh,
        household.LowConfidenceGapDays,
        household.TariffCheckCadenceMonths,
        household.AiPlausibilityEnabled,
        aiPlausibilityBackendOptions.Configured,
        aiPlausibilityBackendOptions.Label);

    private static HouseholdMemberExportDto ToDto(HouseholdMember member) => new(
        member.Id, member.ExternalIssuer, member.ExternalSubjectId, member.DisplayName, member.CreatedAtUtc);

    private static MainMeterExportDto ToDto(MainMeter mainMeter) => new(mainMeter.Id, mainMeter.CreatedAtUtc, mainMeter.DigitCapacityKwh);

    private static MeterReadingExportDto ToDto(MeterReading reading) => new(
        reading.Id, reading.MainMeterId, reading.KwhValue, reading.ReadingTimestamp, reading.IdempotencyKey, reading.CreatedAtUtc);

    // Classification serialized as its lowercase string name (this codebase's manual
    // per-field enum convention — see MeterRegressionClassification.cs's own comment; no global
    // JsonStringEnumConverter is ever added).
    private static MeterRegressionPromptExportDto ToDto(MeterRegressionPrompt prompt) => new(
        prompt.Id,
        prompt.MainMeterId,
        prompt.MeterReadingId,
        prompt.PreviousMeterReadingId,
        prompt.CreatedAtUtc,
        prompt.ResolvedAtUtc,
        prompt.Classification?.ToString().ToLowerInvariant(),
        prompt.DigitCapacityKwh);

    private static TariffExportDto ToDto(Tariff tariff) => new(
        tariff.Id, tariff.MonthlyBaseFee, tariff.PricePerKwh, tariff.Currency, tariff.ContractStartDate, tariff.ContractPeriodMonths, tariff.CreatedAtUtc);

    // TaggedEntityName/CorrelationDirection/CorrelationComputedAtUtc are copied verbatim as opaque
    // by-value snapshot fields (AD-10) — never recomputed against the live tagged entity.
    private static EventExportDto ToDto(Event @event) => new(
        @event.Id,
        @event.Description,
        @event.OccurredAt,
        @event.CreatedAtUtc,
        @event.TaggedEntityType,
        @event.TaggedEntityId,
        @event.TaggedEntityName,
        @event.CorrelationDirection,
        @event.CorrelationComputedAtUtc);

    private static RoomExportDto ToDto(Room room) => new(room.Id, room.Name, room.CreatedAtUtc, room.ArchivedAt);

    private static PowerPointExportDto ToDto(PowerPoint powerPoint) => new(
        powerPoint.Id, powerPoint.RoomId, powerPoint.Name, powerPoint.CreatedAtUtc, powerPoint.ArchivedAt);

    private static DeviceExportDto ToDto(Device device) => new(device.Id, device.PowerPointId, device.Name, device.CreatedAtUtc, device.ArchivedAt);

    // SmartPlugImportId is deliberately omitted — SmartPlugImport itself is out of export scope
    // (transient job-queue metadata), so an exported FK pointing at nothing excluded from this
    // same document would only confuse a reader. RoomName/PowerPointName/DeviceName are the AD-10
    // by-value snapshot; PowerPointId is kept since PowerPoint rows are themselves in scope.
    private static SmartPlugReadingExportDto ToDto(SmartPlugReading reading) => new(
        reading.Id, reading.PowerPointId, reading.RoomName, reading.PowerPointName, reading.DeviceName,
        reading.IntervalStart, reading.IntervalEnd, reading.KwhValue);

    private static StatusSnapshotExportDto ToDto(StatusSnapshot snapshot) => new(
        snapshot.Id,
        snapshot.Status.ToString().ToLowerInvariant(),
        snapshot.PaceToDateKwh,
        snapshot.BaselineToDateKwh,
        snapshot.IsLowConfidence,
        snapshot.ComputedAtUtc);

    private static AuditCorrectionExportDto ToDto(AuditCorrection correction) => new(
        correction.Id, correction.EntityType, correction.EntityId, correction.FieldName, correction.OldValue, correction.NewValue, correction.CorrectedAtUtc);
}

public record HouseholdExportResult(
    string FormatVersion,
    DateTimeOffset ExportedAtUtc,
    HouseholdSettingsExportDto Household,
    IReadOnlyList<HouseholdMemberExportDto> HouseholdMembers,
    MainMeterExportDto? MainMeter,
    IReadOnlyList<MeterReadingExportDto> MeterReadings,
    IReadOnlyList<MeterRegressionPromptExportDto> MeterRegressionPrompts,
    IReadOnlyList<TariffExportDto> Tariffs,
    IReadOnlyList<EventExportDto> Events,
    IReadOnlyList<RoomExportDto> Rooms,
    IReadOnlyList<PowerPointExportDto> PowerPoints,
    IReadOnlyList<DeviceExportDto> Devices,
    IReadOnlyList<SmartPlugReadingExportDto> SmartPlugReadings,
    IReadOnlyList<StatusSnapshotExportDto> StatusSnapshots,
    IReadOnlyList<AuditCorrectionExportDto> AuditCorrections);

public record HouseholdSettingsExportDto(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    string Locale,
    string Currency,
    decimal? YearlyBaselineKwh,
    decimal TrendingThresholdKwh,
    int LowConfidenceGapDays,
    int TariffCheckCadenceMonths,
    bool AiPlausibilityEnabled,
    bool AiPlausibilityBackendConfigured,
    string? AiPlausibilityBackendLabel);

public record HouseholdMemberExportDto(Guid Id, string ExternalIssuer, string ExternalSubjectId, string? DisplayName, DateTimeOffset CreatedAtUtc);

public record MainMeterExportDto(Guid Id, DateTimeOffset CreatedAtUtc, decimal? DigitCapacityKwh);

public record MeterReadingExportDto(
    Guid Id, Guid MainMeterId, decimal KwhValue, DateTimeOffset ReadingTimestamp, Guid IdempotencyKey, DateTimeOffset CreatedAtUtc);

public record MeterRegressionPromptExportDto(
    Guid Id,
    Guid MainMeterId,
    Guid MeterReadingId,
    Guid PreviousMeterReadingId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    string? Classification,
    decimal? DigitCapacityKwh);

public record TariffExportDto(
    Guid Id, decimal MonthlyBaseFee, decimal PricePerKwh, string Currency, DateTimeOffset ContractStartDate, int ContractPeriodMonths, DateTimeOffset CreatedAtUtc);

public record EventExportDto(
    Guid Id,
    string Description,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAtUtc,
    string? TaggedEntityType,
    Guid? TaggedEntityId,
    string? TaggedEntityName,
    string? CorrelationDirection,
    DateTimeOffset? CorrelationComputedAtUtc);

public record RoomExportDto(Guid Id, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? ArchivedAt);

public record PowerPointExportDto(Guid Id, Guid RoomId, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? ArchivedAt);

public record DeviceExportDto(Guid Id, Guid PowerPointId, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? ArchivedAt);

public record SmartPlugReadingExportDto(
    Guid Id,
    Guid? PowerPointId,
    string RoomName,
    string PowerPointName,
    string DeviceName,
    DateTimeOffset IntervalStart,
    DateTimeOffset IntervalEnd,
    decimal KwhValue);

public record StatusSnapshotExportDto(
    Guid Id, string Status, decimal PaceToDateKwh, decimal BaselineToDateKwh, bool IsLowConfidence, DateTimeOffset ComputedAtUtc);

public record AuditCorrectionExportDto(
    Guid Id, string EntityType, Guid EntityId, string FieldName, string OldValue, string NewValue, DateTimeOffset CorrectedAtUtc);
