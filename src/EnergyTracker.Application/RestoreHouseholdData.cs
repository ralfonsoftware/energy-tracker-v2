using System.Text.Json;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

// Mirrors ProcessSmartPlugImportPayload exactly (Task 4) — carries only a temp-file reference,
// never the file bytes (Azure Storage Queue caps a message at 64 KB).
public record RestoreHouseholdDataPayload(string TempFilePath, string OriginalFileName);

/// <summary>Wholesale-replaces the current Household's data with a previously-validated v2 export file (AC #1, #4, #5, #6) — a restore/migration, never a partial-merge edit; never calls IAuditCorrectionRecorder (AD-11's explicit carve-out).</summary>
public class RestoreHouseholdData(IHouseholdRestoreWriter restoreWriter)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(Guid householdId, RestoreHouseholdDataPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            // Task 1's two-phase flow already ran ValidateHouseholdImport's full structural check
            // synchronously before this job was ever enqueued — this is not a second validation
            // pass. A missing file here means the rare validate-then-confirm race (e.g. a
            // container restart clearing temp disk between the two requests), not bad file
            // content — HouseholdImportValidationException's .Message is safe to forward to the
            // client either way (BackgroundJobProcessor.cs).
            if (!File.Exists(payload.TempFilePath))
            {
                throw new HouseholdImportValidationException(
                    $"The uploaded file '{payload.OriginalFileName}' could not be found on the server — please upload it again.");
            }

            var json = await File.ReadAllTextAsync(payload.TempFilePath, cancellationToken);
            var importData = JsonSerializer.Deserialize<HouseholdExportResult>(json, SerializerOptions)
                ?? throw new HouseholdImportValidationException($"The uploaded file '{payload.OriginalFileName}' deserialized to nothing.");

            await restoreWriter.RestoreAsync(ToRestoreData(householdId, importData), cancellationToken);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && File.Exists(payload.TempFilePath))
            {
                File.Delete(payload.TempFilePath);
            }
        }
    }

    private static HouseholdRestoreData ToRestoreData(Guid householdId, HouseholdExportResult importData) => new(
        householdId,
        new HouseholdSettingsPatch(
            importData.Household.Locale,
            importData.Household.Currency,
            importData.Household.YearlyBaselineKwh,
            importData.Household.TrendingThresholdKwh,
            importData.Household.LowConfidenceGapDays,
            importData.Household.TariffCheckCadenceMonths,
            importData.Household.AiPlausibilityEnabled),
        importData.HouseholdMembers.Select(m => ToEntity(m, householdId)).ToList(),
        importData.MainMeter is { } mainMeter ? ToEntity(mainMeter, householdId) : null,
        importData.Rooms.Select(r => ToEntity(r, householdId)).ToList(),
        importData.PowerPoints.Select(p => ToEntity(p, householdId)).ToList(),
        importData.Devices.Select(d => ToEntity(d, householdId)).ToList(),
        importData.MeterReadings.Select(r => ToEntity(r, householdId)).ToList(),
        importData.MeterRegressionPrompts.Select(p => ToEntity(p, householdId)).ToList(),
        importData.Tariffs.Select(t => ToEntity(t, householdId)).ToList(),
        importData.Events.Select(e => ToEntity(e, householdId)).ToList(),
        importData.SmartPlugReadings.Select(r => ToEntity(r, householdId)).ToList(),
        importData.StatusSnapshots.Select(s => ToEntity(s, householdId)).ToList(),
        importData.AuditCorrections.Select(c => ToEntity(c, householdId)).ToList());

    private static HouseholdMember ToEntity(HouseholdMemberExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        ExternalIssuer = dto.ExternalIssuer,
        ExternalSubjectId = dto.ExternalSubjectId,
        DisplayName = dto.DisplayName,
        CreatedAtUtc = dto.CreatedAtUtc,
    };

    private static MainMeter ToEntity(MainMeterExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        CreatedAtUtc = dto.CreatedAtUtc,
        DigitCapacityKwh = dto.DigitCapacityKwh,
    };

    private static Room ToEntity(RoomExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        Name = dto.Name,
        CreatedAtUtc = dto.CreatedAtUtc,
        ArchivedAt = dto.ArchivedAt,
    };

    // RoomId is reused byte-for-byte from the file (Dev Notes "Restore write-target design") — the
    // file's internal FK graph is already self-consistent; only HouseholdId is ever remapped.
    private static PowerPoint ToEntity(PowerPointExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        RoomId = dto.RoomId,
        Name = dto.Name,
        CreatedAtUtc = dto.CreatedAtUtc,
        ArchivedAt = dto.ArchivedAt,
    };

    private static Device ToEntity(DeviceExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        PowerPointId = dto.PowerPointId,
        Name = dto.Name,
        CreatedAtUtc = dto.CreatedAtUtc,
        ArchivedAt = dto.ArchivedAt,
    };

    private static MeterReading ToEntity(MeterReadingExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        MainMeterId = dto.MainMeterId,
        KwhValue = dto.KwhValue,
        ReadingTimestamp = dto.ReadingTimestamp,
        IdempotencyKey = dto.IdempotencyKey,
        CreatedAtUtc = dto.CreatedAtUtc,
    };

    // Classification round-trips case-insensitively against ExportHouseholdData's own lowercase
    // convention — Task 2's validator already rejected anything that doesn't parse this way, so
    // Enum.Parse here can never throw.
    private static MeterRegressionPrompt ToEntity(MeterRegressionPromptExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        MainMeterId = dto.MainMeterId,
        MeterReadingId = dto.MeterReadingId,
        PreviousMeterReadingId = dto.PreviousMeterReadingId,
        CreatedAtUtc = dto.CreatedAtUtc,
        ResolvedAtUtc = dto.ResolvedAtUtc,
        Classification = dto.Classification is { } c ? Enum.Parse<MeterRegressionClassification>(c, ignoreCase: true) : null,
        DigitCapacityKwh = dto.DigitCapacityKwh,
    };

    private static Tariff ToEntity(TariffExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        MonthlyBaseFee = dto.MonthlyBaseFee,
        PricePerKwh = dto.PricePerKwh,
        Currency = dto.Currency,
        ContractStartDate = dto.ContractStartDate,
        ContractPeriodMonths = dto.ContractPeriodMonths,
        CreatedAtUtc = dto.CreatedAtUtc,
    };

    private static Event ToEntity(EventExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        Description = dto.Description,
        OccurredAt = dto.OccurredAt,
        CreatedAtUtc = dto.CreatedAtUtc,
        TaggedEntityType = dto.TaggedEntityType,
        TaggedEntityId = dto.TaggedEntityId,
        TaggedEntityName = dto.TaggedEntityName,
        CorrelationDirection = dto.CorrelationDirection,
        CorrelationComputedAtUtc = dto.CorrelationComputedAtUtc,
    };

    // SmartPlugImportId is always null on insert — the field isn't in the export at all
    // (SmartPlugImport stays out of scope, unchanged from Story 7.1).
    private static SmartPlugReading ToEntity(SmartPlugReadingExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        SmartPlugImportId = null,
        PowerPointId = dto.PowerPointId,
        RoomName = dto.RoomName,
        PowerPointName = dto.PowerPointName,
        DeviceName = dto.DeviceName,
        IntervalStart = dto.IntervalStart,
        IntervalEnd = dto.IntervalEnd,
        KwhValue = dto.KwhValue,
    };

    private static StatusSnapshot ToEntity(StatusSnapshotExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        Status = Enum.Parse<Status>(dto.Status, ignoreCase: true),
        PaceToDateKwh = dto.PaceToDateKwh,
        BaselineToDateKwh = dto.BaselineToDateKwh,
        IsLowConfidence = dto.IsLowConfidence,
        ComputedAtUtc = dto.ComputedAtUtc,
    };

    private static AuditCorrection ToEntity(AuditCorrectionExportDto dto, Guid householdId) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        EntityType = dto.EntityType,
        EntityId = dto.EntityId,
        FieldName = dto.FieldName,
        OldValue = dto.OldValue,
        NewValue = dto.NewValue,
        CorrectedAtUtc = dto.CorrectedAtUtc,
    };
}

// Mirrors SmartPlugImportValidationException's precedent (ProcessSmartPlugImport.cs) — its
// .Message is deliberately user-facing and safe to forward verbatim as BackgroundJob.ErrorMessage
// (BackgroundJobProcessor.cs's round-4 incident-fix convention).
public class HouseholdImportValidationException(string message) : Exception(message);
