using System.Runtime.InteropServices;
using System.Text.Json;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

// Mirrors ProcessSmartPlugImportPayload exactly (Task 4) — carries only a temp-file reference,
// never the file bytes (Azure Storage Queue caps a message at 64 KB).
public record RestoreHouseholdDataPayload(string TempFilePath, string OriginalFileName);

/// <summary>Wholesale-replaces the current Household's data with a previously-validated v2 export file (AC #1, #4, #5, #6) — a restore/migration, never a partial-merge edit; assigns new ids and remaps references so a file can never collide with another Household (audit C10); never calls IAuditCorrectionRecorder (AD-11's explicit carve-out); never writes HouseholdMember rows, so a file can neither grant nor revoke access (AD-25).</summary>
public class RestoreHouseholdData(IHouseholdRestoreWriter restoreWriter, RestoreMemoryBudget memoryBudget)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(Guid householdId, RestoreHouseholdDataPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            // Task 1's two-phase flow already ran ValidateHouseholdImport's full structural check
            // synchronously before this job was ever enqueued — this is not a second validation
            // pass. A missing file here means the enqueue-then-dequeue window (e.g. a replica or its
            // disk replaced, AD-24), not bad file content. It ends the job with the stable
            // upload-missing code (AD-6), never an English sentence forwarded to the client.
            if (!File.Exists(payload.TempFilePath))
            {
                throw new UploadMissingException($"The uploaded file '{payload.OriginalFileName}' no longer exists on the server.");
            }

            // Story 11.25 (AD-24): the object graph below costs two to three times the file's size in memory
            // (RestoreMemoryBudget), and RestoreAsync starts by deleting the Household's data. A file this
            // process cannot hold therefore ends here, before anything is deleted, with a stable code.
            var fileBytes = new FileInfo(payload.TempFilePath).Length;
            if (fileBytes > memoryBudget.MaxExportBytes)
            {
                throw new RestoreTooLargeException(
                    $"The uploaded file '{payload.OriginalFileName}' is {fileBytes} bytes, above the {memoryBudget.MaxExportBytes} bytes this server can restore.");
            }

            // Story 11.10 (audit S4): deserialized from the file stream, never from a string (which would
            // hold the file twice as UTF-16). The object graph still materializes because the restore
            // writer takes the whole HouseholdRestoreData, so this halves the transient peak but does
            // not bound it. The stream overload skips a leading UTF-8 BOM, as the validator does.
            HouseholdExportResult importData;
            await using (var file = new FileStream(
                payload.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                importData = await JsonSerializer.DeserializeAsync<HouseholdExportResult>(file, SerializerOptions, cancellationToken)
                    ?? throw new HouseholdImportValidationException($"The uploaded file '{payload.OriginalFileName}' deserialized to nothing.");
            }

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

    // Story 11.14 (audit C10): every entity gets a new id and every reference follows its target before an
    // entity is built, so ToEntity below only copies (it never decides an id). One category at a time, and
    // each DTO is dropped as soon as its entity exists (Story 11.25), so the DTO list and the entity list
    // are never both complete: the smart plug list holds nearly all of a large export.
    private static HouseholdRestoreData ToRestoreData(Guid householdId, HouseholdExportResult importData)
    {
        var rekey = new HouseholdRestoreRekey(importData);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        return new(
            householdId,
            new HouseholdSettingsPatch(
                importData.Household.Locale,
                importData.Household.Currency,
                importData.Household.YearlyBaselineKwh,
                importData.Household.TrendingThresholdKwh,
                importData.Household.LowConfidenceGapDays,
                importData.Household.TariffCheckCadenceMonths,
                importData.Household.AiPlausibilityEnabled),
            importData.MainMeter is { } mainMeter ? ToEntity(rekey.Rekey(mainMeter), householdId) : null,
            ConvertAndRelease(importData.Rooms, r => ToEntity(rekey.Rekey(r), householdId)),
            ConvertAndRelease(importData.PowerPoints, p => ToEntity(rekey.Rekey(p), householdId)),
            ConvertAndRelease(importData.Devices, d => ToEntity(rekey.Rekey(d), householdId)),
            ConvertAndRelease(importData.MeterReadings, r => ToEntity(rekey.Rekey(r), householdId)),
            ConvertAndRelease(importData.MeterRegressionPrompts, p => ToEntity(rekey.Rekey(p), householdId)),
            ConvertAndRelease(importData.Tariffs, t => ToEntity(rekey.Rekey(t), householdId)),
            ConvertAndRelease(importData.Events, e => ToEntity(rekey.Rekey(e), householdId)),
            ConvertAndRelease(importData.SmartPlugReadings, r => ToEntity(rekey.Rekey(r), householdId, names)),
            ConvertAndRelease(importData.StatusSnapshots, s => ToEntity(rekey.Rekey(s), householdId)),
            ConvertAndRelease(importData.AuditCorrections, c => ToEntity(rekey.Rekey(c), householdId)));
    }

    // A List<T> (what the deserializer produces) is emptied in place as it is read, so each DTO becomes
    // garbage before the next entity is built; any other list is only enumerated.
    private static List<TEntity> ConvertAndRelease<TDto, TEntity>(IReadOnlyList<TDto> source, Func<TDto, TEntity> convert)
        where TDto : class
    {
        var result = new List<TEntity>(source.Count);
        if (source is List<TDto> list)
        {
            var items = CollectionsMarshal.AsSpan(list);
            for (var i = 0; i < items.Length; i++)
            {
                result.Add(convert(items[i]));
                items[i] = null!;
            }
        }
        else
        {
            result.AddRange(source.Select(convert));
        }

        return result;
    }

    private static string Share(Dictionary<string, string> names, string value) =>
        names.TryGetValue(value, out var shared) ? shared : names[value] = value;

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

    // The dto handed in here is already re-keyed (HouseholdRestoreRekey): Id and RoomId are the new
    // values, HouseholdId is the current Household's.
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
    // Names repeat on every reading of a device (the same three strings 500,000 times), so equal names
    // share one instance instead of one copy per row.
    private static SmartPlugReading ToEntity(SmartPlugReadingExportDto dto, Guid householdId, Dictionary<string, string> names) => new()
    {
        Id = dto.Id,
        HouseholdId = householdId,
        SmartPlugImportId = null,
        PowerPointId = dto.PowerPointId,
        RoomName = Share(names, dto.RoomName),
        PowerPointName = Share(names, dto.PowerPointName),
        DeviceName = Share(names, dto.DeviceName),
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
