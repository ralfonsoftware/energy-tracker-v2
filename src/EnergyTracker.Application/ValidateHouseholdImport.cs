using System.Text.Json;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

public record HouseholdImportValidationResult(bool IsValid, IReadOnlyList<string> Failures, HouseholdImportInventory? Inventory)
{
    public static HouseholdImportValidationResult Valid(HouseholdImportInventory inventory) => new(true, [], inventory);

    public static HouseholdImportValidationResult Invalid(IReadOnlyList<string> failures) => new(false, failures, null);
}

// Story 11.10 (audit S4): what the upload endpoint needs to show before the member confirms — counts
// and a bounded list of display names — instead of the whole deserialized export. The restore job
// re-reads the file itself.
public record HouseholdImportInventory(
    int HouseholdMembers,
    bool HasMainMeter,
    int MeterReadings,
    int MeterRegressionPrompts,
    int Tariffs,
    int Events,
    int Rooms,
    int PowerPoints,
    int Devices,
    int SmartPlugReadings,
    int StatusSnapshots,
    int AuditCorrections,
    IReadOnlyList<string> MemberDisplayNames);

/// <summary>Structurally validates an uploaded v2 export file as a stream, in two passes and with bounded memory, before any DB write happens (AC #1, #2, #3) — collects every failure (the first 200 are kept) instead of failing fast; performs no DB write of any kind, satisfying "never partially applied" structurally.</summary>
public class ValidateHouseholdImport
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // Bounds on what is retained, not extra rejections (Story 11.10, AC #7): a valid file is never
    // refused for having many failures or members, only the report and the summary are capped.
    internal const int MaxReportedFailures = 200;
    internal const int MaxRetainedMemberNames = 100;
    internal const int MaxMemberNameLength = 200;

    internal const string ValueTooLargeFailure = "A single value in the file is larger than 4 MiB, which no export produces.";

    private const string ExportedAtUtc = "exportedAtUtc";
    private const string HouseholdField = "household";
    private const string MainMeterField = "mainMeter";

    // The validator's fixed field order: failures are reported in this order whatever the file's own
    // property order is, as they were when the whole document was random-access.
    private static readonly string[] FieldOrder =
    [
        ExportedAtUtc, HouseholdField, "householdMembers", MainMeterField, "meterReadings", "meterRegressionPrompts", "tariffs",
        "events", "rooms", "powerPoints", "devices", "smartPlugReadings", "statusSnapshots", "auditCorrections",
    ];

    // Validates the file at stream's current content. The stream must be seekable (the caller passes a
    // FileStream): the first pass proves the file is well-formed and collects the ids that references
    // point at, which may sit later in the file; the second pass validates each array element as it
    // streams by. Never copies the stream into memory.
    public HouseholdImportValidationResult Execute(Stream stream)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must be seekable: the file is read twice and never buffered whole.", nameof(stream));
        }

        try
        {
            using var reader = new HouseholdImportJsonReader(stream);
            var prescan = Prescan(reader);
            if (prescan.Failure is not null)
            {
                return HouseholdImportValidationResult.Invalid([prescan.Failure]);
            }

            reader.Rewind();
            return ValidateElements(reader, prescan);
        }
        catch (JsonException ex)
        {
            // Malformed JSON anywhere is the single failure, never the partial structural failures
            // collected before it.
            return HouseholdImportValidationResult.Invalid([$"The file is not valid JSON: {ex.Message}"]);
        }
        catch (HouseholdImportJsonReader.TooLargeException)
        {
            return HouseholdImportValidationResult.Invalid([ValueTooLargeFailure]);
        }
    }

    private sealed record PrescanResult(
        string? Failure, Guid? MainMeterId, HashSet<Guid> MeterReadingIds, HashSet<Guid> RoomIds, HashSet<Guid> PowerPointIds);

    // Pass 1: walks the whole file once. Proves it is well-formed (a JsonException anywhere is the
    // single not-valid-JSON failure), reads formatVersion wherever it sits, and collects the id sets the
    // reference checks need. These sets are the one place memory still grows with the file, by item
    // count and not by byte size (Story 11.10, AC #7d).
    private static PrescanResult Prescan(HouseholdImportJsonReader reader)
    {
        var meterReadingIds = new HashSet<Guid>();
        var roomIds = new HashSet<Guid>();
        var powerPointIds = new HashSet<Guid>();

        var first = reader.ReadToken();
        if (first != JsonTokenType.StartObject)
        {
            reader.SkipRestOfValue(first);
            reader.FinishDocument();
            return new PrescanResult("The file's top level must be a JSON object.", null, meterReadingIds, roomIds, powerPointIds);
        }

        string? formatVersion = null;
        Guid? mainMeterId = null;
        while (reader.ReadToken(captureName: true) != JsonTokenType.EndObject)
        {
            switch (reader.PropertyName)
            {
                case "formatVersion" when reader.PeekToken() == JsonTokenType.String:
                    using (var formatVersionDocument = reader.ReadValue())
                    {
                        formatVersion = formatVersionDocument.RootElement.GetString();
                    }

                    break;
                case MainMeterField when reader.PeekToken() == JsonTokenType.StartObject:
                    using (var mainMeterDocument = reader.ReadValue())
                    {
                        mainMeterId = TryGetGuid(mainMeterDocument.RootElement, "id");
                    }

                    break;
                case "meterReadings":
                    CollectIds(reader, meterReadingIds);
                    break;
                case "rooms":
                    CollectIds(reader, roomIds);
                    break;
                case "powerPoints":
                    CollectIds(reader, powerPointIds);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        reader.FinishDocument();

        // AC #3: v2-to-v2 only — no v1 read/convert path of any kind. Checked first and
        // exclusively when it fails: a genuine v1 file has none of the fields validated below,
        // so reporting a page of unrelated "missing field" failures on top of the real problem
        // would bury it.
        if (formatVersion != ExportHouseholdData.FormatVersion)
        {
            return new PrescanResult(
                $"Unsupported formatVersion '{formatVersion ?? "(missing)"}'. Only '{ExportHouseholdData.FormatVersion}' is accepted.",
                null, meterReadingIds, roomIds, powerPointIds);
        }

        return new PrescanResult(null, mainMeterId, meterReadingIds, roomIds, powerPointIds);
    }

    // Collects the ids of every well-formed item in an array field, for cross-entity reference
    // checks — an item with a missing/malformed id is silently skipped here (pass 2's own RequireGuid
    // check on that same field reports it as its own failure). Anything that is not an array is skipped
    // token by token; pass 2 reports it.
    private static void CollectIds(HouseholdImportJsonReader reader, HashSet<Guid> ids)
    {
        if (reader.PeekToken() != JsonTokenType.StartArray)
        {
            reader.SkipValue();
            return;
        }

        reader.ReadToken();
        while (reader.TryReadArrayElement(out var element))
        {
            using (element)
            {
                if (element.RootElement.ValueKind == JsonValueKind.Object && TryGetGuid(element.RootElement, "id") is { } id)
                {
                    ids.Add(id);
                }
            }
        }
    }

    // One field's failures: the first MaxReportedFailures are kept, the rest only counted, so the
    // report is bounded however broken the file is.
    private sealed class FailureBucket
    {
        public List<string> Items { get; } = [];

        public long Total { get; private set; }

        public void Add(string failure)
        {
            Total++;
            if (Items.Count < MaxReportedFailures)
            {
                Items.Add(failure);
            }
        }
    }

    private sealed record ArraySpec(
        Action<JsonElement, string, List<string>> Validate, Action<JsonElement> Parse, Action<JsonElement>? Observe = null);

    // Pass 2: each array element is validated as it streams by, in file order, with the per-item rules
    // below. Failures land in per-field buckets and are emitted in FieldOrder.
    private static HouseholdImportValidationResult ValidateElements(HouseholdImportJsonReader reader, PrescanResult prescan)
    {
        var buckets = FieldOrder.ToDictionary(field => field, _ => new FailureBucket());
        var counts = FieldOrder.ToDictionary(field => field, _ => 0);
        var memberNames = new List<string>();
        var hasMainMeter = false;
        var seen = new HashSet<string>();

        var arrays = new Dictionary<string, ArraySpec>
        {
            ["householdMembers"] = new(ValidateHouseholdMember, Check<HouseholdMemberExportDto>, item => RetainMemberName(item, memberNames)),
            ["meterReadings"] = new((item, path, f) => ValidateMeterReading(item, path, f, prescan.MainMeterId), Check<MeterReadingExportDto>),
            ["meterRegressionPrompts"] = new(
                (item, path, f) => ValidateMeterRegressionPrompt(item, path, f, prescan.MainMeterId, prescan.MeterReadingIds),
                Check<MeterRegressionPromptExportDto>),
            ["tariffs"] = new(ValidateTariff, Check<TariffExportDto>),
            ["events"] = new(ValidateEvent, Check<EventExportDto>),
            ["rooms"] = new(ValidateRoom, Check<RoomExportDto>),
            ["powerPoints"] = new((item, path, f) => ValidatePowerPoint(item, path, f, prescan.RoomIds), Check<PowerPointExportDto>),
            ["devices"] = new((item, path, f) => ValidateDevice(item, path, f, prescan.PowerPointIds), Check<DeviceExportDto>),
            ["smartPlugReadings"] = new(
                (item, path, f) => ValidateSmartPlugReading(item, path, f, prescan.PowerPointIds), Check<SmartPlugReadingExportDto>),
            ["statusSnapshots"] = new(ValidateStatusSnapshot, Check<StatusSnapshotExportDto>),
            ["auditCorrections"] = new(ValidateAuditCorrection, Check<AuditCorrectionExportDto>),
        };

        reader.ReadToken();
        var scratch = new List<string>();
        while (reader.ReadToken(captureName: true) != JsonTokenType.EndObject)
        {
            var field = reader.PropertyName!;
            if (!buckets.TryGetValue(field, out var bucket))
            {
                reader.SkipValue();
                continue;
            }

            seen.Add(field);
            if (field == ExportedAtUtc)
            {
                ValidateExportedAt(ReadSmallValue(reader), bucket);
            }
            else if (field == HouseholdField)
            {
                ValidateHouseholdSettings(ReadSmallValue(reader), scratch);
                Flush(scratch, bucket);
            }
            else if (field == MainMeterField)
            {
                using var document = ReadSmallValue(reader);
                hasMainMeter = document?.RootElement.ValueKind == JsonValueKind.Object;
                ValidateMainMeter(document?.RootElement ?? default, scratch);
                Flush(scratch, bucket);
            }
            else
            {
                counts[field] += ValidateArray(reader, field, arrays[field], bucket, scratch);
            }
        }

        // A field the file never mentions: reported exactly as a field of the wrong kind is.
        foreach (var field in FieldOrder.Where(f => !seen.Contains(f)))
        {
            if (field == ExportedAtUtc)
            {
                ValidateExportedAt(null, buckets[field]);
            }
            else if (field == HouseholdField)
            {
                ValidateHouseholdSettings(null, scratch);
                Flush(scratch, buckets[field]);
            }
            else if (field != MainMeterField)
            {
                buckets[field].Add($"'{field}' must be an array.");
            }
        }

        var total = buckets.Values.Sum(b => b.Total);
        if (total == 0)
        {
            return HouseholdImportValidationResult.Valid(new HouseholdImportInventory(
                counts["householdMembers"], hasMainMeter, counts["meterReadings"], counts["meterRegressionPrompts"], counts["tariffs"],
                counts["events"], counts["rooms"], counts["powerPoints"], counts["devices"], counts["smartPlugReadings"],
                counts["statusSnapshots"], counts["auditCorrections"], memberNames));
        }

        var failures = FieldOrder.SelectMany(field => buckets[field].Items).Take(MaxReportedFailures).ToList();
        if (total > MaxReportedFailures)
        {
            failures.Add($"… and {total - MaxReportedFailures} more failures not shown.");
        }

        return HouseholdImportValidationResult.Invalid(failures);
    }

    private static void Flush(List<string> scratch, FailureBucket bucket)
    {
        foreach (var failure in scratch)
        {
            bucket.Add(failure);
        }

        scratch.Clear();
    }

    // A root-level singleton (exportedAtUtc, household, mainMeter): a small value read whole. A
    // container where a scalar belongs, or an array where an object belongs, is crossed token by token
    // and reported as the wrong kind (null), so a hostile huge value costs nothing.
    private static JsonDocument? ReadSmallValue(HouseholdImportJsonReader reader)
    {
        if (reader.PeekToken() == JsonTokenType.StartArray)
        {
            reader.SkipValue();
            return null;
        }

        return reader.ReadValue();
    }

    private static void ValidateExportedAt(JsonDocument? document, FailureBucket bucket)
    {
        using (document)
        {
            if (document is null || document.RootElement.ValueKind != JsonValueKind.String || !document.RootElement.TryGetDateTimeOffset(out _))
            {
                bucket.Add("(root): 'exportedAtUtc' must be an ISO 8601 timestamp.");
            }
        }
    }

    private static int ValidateArray(
        HouseholdImportJsonReader reader, string field, ArraySpec spec, FailureBucket bucket, List<string> scratch)
    {
        if (reader.PeekToken() != JsonTokenType.StartArray)
        {
            reader.SkipValue();
            bucket.Add($"'{field}' must be an array.");
            return 0;
        }

        reader.ReadToken();
        var index = 0;
        var seenIds = new HashSet<Guid>();
        while (reader.TryReadArrayElement(out var document))
        {
            using (document)
            {
                var item = document.RootElement;
                var path = $"{field}[{index}]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    bucket.Add($"{path}: must be an object.");
                }
                else
                {
                    spec.Validate(item, path, scratch);

                    // Every field the checks above covered has been shape-, range-, reference- and
                    // enum-membership-checked, still wrapped defensively rather than trusted blindly:
                    // System.Text.Json's exact parsing rules aren't byte-for-byte re-derived by hand
                    // (Code Review, Story 7.2 Pass 1: this used to be an unguarded call that could throw
                    // on a technically-valid-per-the-checks-but-STJ-rejects value and surface as an
                    // unhandled exception instead of a reported failure). Per item now that no whole-file
                    // deserialization happens at validation time (Story 11.10), so the restore job never
                    // meets a surprise.
                    if (scratch.Count == 0)
                    {
                        try
                        {
                            spec.Parse(item);
                        }
                        catch (JsonException ex)
                        {
                            scratch.Add($"The file passed structural validation but could not be parsed: {path}: {ex.Message}");
                        }
                    }

                    // Every entity category here carries its own "id" — a duplicate collides on the
                    // DB primary key during HouseholdRestoreWriter.ChunkedInsertAsync, which reports it
                    // as an opaque DB exception rather than a reported validation failure. Checked
                    // generically here rather than per-entity-type, since every Validate* method
                    // already requires an "id" field the same way.
                    if (TryGetGuid(item, "id") is { } id && !seenIds.Add(id))
                    {
                        scratch.Add($"{path}: 'id' {id} is a duplicate of another entry in '{field}'.");
                    }

                    spec.Observe?.Invoke(item);
                    Flush(scratch, bucket);
                }
            }

            index++;
        }

        return index;
    }

    private static void Check<TDto>(JsonElement item) => item.Deserialize<TDto>(SerializerOptions);

    // Names are untrusted file content: trimmed, blanks dropped, file order and duplicates kept (the
    // people-in-this-backup panel shows them as plain text). Only the first MaxRetainedMemberNames are
    // kept, each truncated, so a file of a million members cannot grow the summary.
    private static void RetainMemberName(JsonElement member, List<string> names)
    {
        if (names.Count >= MaxRetainedMemberNames
            || !member.TryGetProperty("displayName", out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var name = element.GetString()!.Trim();
        if (name.Length > 0)
        {
            names.Add(name.Length > MaxMemberNameLength ? name[..MaxMemberNameLength] : name);
        }
    }

    private static void ValidateHouseholdSettings(JsonDocument? document, List<string> failures)
    {
        using (document)
        {
            ValidateHouseholdSettings(document?.RootElement ?? default, failures);
        }
    }

    private static void ValidateHouseholdSettings(JsonElement household, List<string> failures)
    {
        if (household.ValueKind != JsonValueKind.Object)
        {
            failures.Add("'household' must be an object.");
            return;
        }

        const string path = "household";
        RequireGuid(household, "id", path, failures);
        RequireDateTimeOffset(household, "createdAtUtc", path, failures);
        RequireString(household, "locale", path, failures);
        RequireString(household, "currency", path, failures);
        RequireOptionalNonNegativeDecimal(household, "yearlyBaselineKwh", path, failures);
        RequireNonNegativeDecimal(household, "trendingThresholdKwh", path, failures);
        RequireNonNegativeInt(household, "lowConfidenceGapDays", path, failures);
        RequirePositiveInt(household, "tariffCheckCadenceMonths", path, failures);
        RequireBool(household, "aiPlausibilityEnabled", path, failures);
        RequireBool(household, "aiPlausibilityBackendConfigured", path, failures);
        RequireOptionalString(household, "aiPlausibilityBackendLabel", path, failures);
        RequireDeserializable<HouseholdSettingsExportDto>(household, path, failures);
    }

    // A missing, undefined or null mainMeter is valid (no reading was ever logged).
    private static void ValidateMainMeter(JsonElement element, List<string> failures)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            failures.Add("'mainMeter' must be an object or null.");
            return;
        }

        const string path = "mainMeter";
        RequireGuid(element, "id", path, failures);
        RequireDateTimeOffset(element, "createdAtUtc", path, failures);
        RequireOptionalNonNegativeDecimal(element, "digitCapacityKwh", path, failures);

        RequireDeserializable<MainMeterExportDto>(element, path, failures);
    }

    // The per-item deserialization safety net for the two root-level objects (see ValidateArray).
    private static void RequireDeserializable<TDto>(JsonElement element, string path, List<string> failures)
    {
        if (failures.Count > 0)
        {
            return;
        }

        try
        {
            element.Deserialize<TDto>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            failures.Add($"The file passed structural validation but could not be parsed: {path}: {ex.Message}");
        }
    }

    private static void ValidateHouseholdMember(JsonElement item, string path, List<string> failures)
    {
        // AD-25: restore never writes members, so only `id` and the optional `displayName` (shown
        // as "people in this backup") are validated. Pre-change files still carry externalIssuer,
        // externalSubjectId and createdAtUtc; they are deliberately not validated (accepted and
        // ignored), so an old export keeps restoring.
        RequireGuid(item, "id", path, failures);
        RequireOptionalString(item, "displayName", path, failures);
    }

    private static void ValidateMeterReading(JsonElement item, string path, List<string> failures, Guid? mainMeterId)
    {
        RequireGuid(item, "id", path, failures);
        RequireGuid(item, "mainMeterId", path, failures);
        RequireNonNegativeDecimal(item, "kwhValue", path, failures);
        RequireDateTimeOffset(item, "readingTimestamp", path, failures);
        RequireGuid(item, "idempotencyKey", path, failures);
        RequireDateTimeOffset(item, "createdAtUtc", path, failures);

        RequireMainMeterReference(item, "mainMeterId", path, mainMeterId, failures);
    }

    private static void ValidateMeterRegressionPrompt(
        JsonElement item, string path, List<string> failures, Guid? mainMeterId, HashSet<Guid> meterReadingIds)
    {
        RequireGuid(item, "id", path, failures);
        RequireGuid(item, "mainMeterId", path, failures);
        RequireGuid(item, "meterReadingId", path, failures);
        RequireGuid(item, "previousMeterReadingId", path, failures);
        RequireDateTimeOffset(item, "createdAtUtc", path, failures);
        RequireOptionalDateTimeOffset(item, "resolvedAtUtc", path, failures);
        // Must round-trip against the exact lowercase names ExportHouseholdData.cs emits,
        // case-insensitively — an unrecognized value is a reported failure, never a silent default.
        RequireOptionalEnum<MeterRegressionClassification>(item, "classification", path, failures);
        RequireOptionalNonNegativeDecimal(item, "digitCapacityKwh", path, failures);

        RequireMainMeterReference(item, "mainMeterId", path, mainMeterId, failures);
        RequireReference(item, "meterReadingId", path, meterReadingIds, "meterReading", failures);
        RequireReference(item, "previousMeterReadingId", path, meterReadingIds, "meterReading", failures);
    }

    private static void ValidateTariff(JsonElement item, string path, List<string> failures)
    {
        RequireGuid(item, "id", path, failures);
        RequireNonNegativeDecimal(item, "monthlyBaseFee", path, failures);
        RequireNonNegativeDecimal(item, "pricePerKwh", path, failures);
        RequireString(item, "currency", path, failures);
        RequireDateTimeOffset(item, "contractStartDate", path, failures);
        RequirePositiveInt(item, "contractPeriodMonths", path, failures);
        RequireDateTimeOffset(item, "createdAtUtc", path, failures);
    }

    private static void ValidateEvent(JsonElement item, string path, List<string> failures)
    {
        RequireGuid(item, "id", path, failures);
        RequireString(item, "description", path, failures);
        RequireDateTimeOffset(item, "occurredAt", path, failures);
        RequireDateTimeOffset(item, "createdAtUtc", path, failures);
        RequireOptionalString(item, "taggedEntityType", path, failures);
        RequireOptionalGuid(item, "taggedEntityId", path, failures);
        RequireOptionalString(item, "taggedEntityName", path, failures);
        RequireOptionalString(item, "correlationDirection", path, failures);
        RequireOptionalDateTimeOffset(item, "correlationComputedAtUtc", path, failures);
    }

    private static void ValidateRoom(JsonElement item, string path, List<string> failures)
    {
        RequireGuid(item, "id", path, failures);
        RequireString(item, "name", path, failures);
        RequireDateTimeOffset(item, "createdAtUtc", path, failures);
        RequireOptionalDateTimeOffset(item, "archivedAt", path, failures);
    }

    private static void ValidatePowerPoint(JsonElement item, string path, List<string> failures, HashSet<Guid> roomIds)
    {
        RequireGuid(item, "id", path, failures);
        RequireGuid(item, "roomId", path, failures);
        RequireString(item, "name", path, failures);
        RequireDateTimeOffset(item, "createdAtUtc", path, failures);
        RequireOptionalDateTimeOffset(item, "archivedAt", path, failures);

        RequireReference(item, "roomId", path, roomIds, "room", failures);
    }

    private static void ValidateDevice(JsonElement item, string path, List<string> failures, HashSet<Guid> powerPointIds)
    {
        RequireGuid(item, "id", path, failures);
        RequireGuid(item, "powerPointId", path, failures);
        RequireString(item, "name", path, failures);
        RequireDateTimeOffset(item, "createdAtUtc", path, failures);
        RequireOptionalDateTimeOffset(item, "archivedAt", path, failures);

        RequireReference(item, "powerPointId", path, powerPointIds, "powerPoint", failures);
    }

    private static void ValidateSmartPlugReading(JsonElement item, string path, List<string> failures, HashSet<Guid> powerPointIds)
    {
        RequireGuid(item, "id", path, failures);
        RequireOptionalGuid(item, "powerPointId", path, failures);
        RequireString(item, "roomName", path, failures);
        RequireString(item, "powerPointName", path, failures);
        RequireStringMaxLength(item, "deviceName", path, SmartPlugReading.DeviceNameMaxLength, failures);
        RequireDateTimeOffset(item, "intervalStart", path, failures);
        RequireDateTimeOffset(item, "intervalEnd", path, failures);
        RequireNonNegativeDecimal(item, "kwhValue", path, failures);

        // powerPointId is optional (AC #7's whole-file FlaggedForReview case never resolves one) —
        // RequireReference already skips the check entirely when the field is absent/null.
        RequireReference(item, "powerPointId", path, powerPointIds, "powerPoint", failures);
    }

    private static void ValidateStatusSnapshot(JsonElement item, string path, List<string> failures)
    {
        RequireGuid(item, "id", path, failures);
        RequireEnum<Status>(item, "status", path, failures);
        RequireDecimal(item, "paceToDateKwh", path, failures);
        RequireDecimal(item, "baselineToDateKwh", path, failures);
        RequireBool(item, "isLowConfidence", path, failures);
        RequireDateTimeOffset(item, "computedAtUtc", path, failures);
    }

    private static void ValidateAuditCorrection(JsonElement item, string path, List<string> failures)
    {
        RequireGuid(item, "id", path, failures);
        RequireString(item, "entityType", path, failures);
        RequireGuid(item, "entityId", path, failures);
        RequireString(item, "fieldName", path, failures);
        RequireString(item, "oldValue", path, failures);
        RequireString(item, "newValue", path, failures);
        RequireDateTimeOffset(item, "correctedAtUtc", path, failures);
    }

    private static Guid? TryGetGuid(JsonElement obj, string field) =>
        obj.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var value)
            ? value
            : null;

    // A file-internal FK-like reference (e.g. devices[].powerPointId) that doesn't resolve to any
    // id in the referenced array currently only fails deep inside HouseholdRestoreWriter as a raw
    // DB FK-violation exception with no diagnostic value forwarded to the user — this reports it
    // during upload validation instead, alongside every other structural failure (AC #2). Skips
    // silently when the field itself is missing/null/malformed — RequireGuid/RequireOptionalGuid on
    // that same field already reports the shape problem.
    private static void RequireReference(JsonElement item, string field, string path, HashSet<Guid> validIds, string targetDescription, List<string> failures)
    {
        if (TryGetGuid(item, field) is not { } id)
        {
            return;
        }

        if (!validIds.Contains(id))
        {
            failures.Add($"{path}: '{field}' {id} does not match any {targetDescription} in this file.");
        }
    }

    private static void RequireMainMeterReference(JsonElement item, string field, string path, Guid? mainMeterId, List<string> failures)
    {
        if (TryGetGuid(item, field) is not { } id)
        {
            return;
        }

        if (mainMeterId != id)
        {
            failures.Add($"{path}: '{field}' {id} does not match the file's 'mainMeter.id'.");
        }
    }

    private static void RequireGuid(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String || !Guid.TryParse(element.GetString(), out _))
        {
            failures.Add($"{path}: '{field}' must be a GUID string.");
        }
    }

    private static void RequireOptionalGuid(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String || !Guid.TryParse(element.GetString(), out _))
        {
            failures.Add($"{path}: '{field}' must be a GUID string or null.");
        }
    }

    private static void RequireString(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String)
        {
            failures.Add($"{path}: '{field}' must be a string.");
        }
    }

    // A string field with an upper bound (AD-20, Story 11.4: DeviceName is part of the unmapped-reading
    // key, so its column is bounded). JsonElement.GetString().Length counts UTF-16 units, the same unit the
    // parsers use. A missing/non-string value reports the type failure only.
    private static void RequireStringMaxLength(JsonElement obj, string field, string path, int maxLength, List<string> failures)
    {
        RequireString(obj, field, path, failures);
        if (obj.TryGetProperty(field, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString()!.Length > maxLength)
        {
            failures.Add($"{path}: '{field}' must be at most {maxLength} characters.");
        }
    }

    private static void RequireOptionalString(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            failures.Add($"{path}: '{field}' must be a string or null.");
        }
    }

    // JsonElement.TryGetDecimal throws InvalidOperationException (rather than returning false) when
    // ValueKind isn't Number — every RequireXxxDecimal method below must check ValueKind first.
    private static void RequireDecimal(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out _))
        {
            failures.Add($"{path}: '{field}' must be a number.");
        }
    }

    // Fields where a negative value is never legitimate (kWh readings, monetary amounts, cadence
    // days/months) — a structurally valid but semantically nonsensical migration file must not be
    // able to silently overwrite live household config/history with garbage (Code Review, Story
    // 7.2 Pass 1, resolved with Ralf: reject negatives on these fields specifically, not every
    // decimal/int field in the format).
    private static void RequireNonNegativeDecimal(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value))
        {
            failures.Add($"{path}: '{field}' must be a number.");
        }
        else if (value < 0)
        {
            failures.Add($"{path}: '{field}' must not be negative.");
        }
    }

    private static void RequireOptionalNonNegativeDecimal(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var value))
        {
            failures.Add($"{path}: '{field}' must be a number or null.");
        }
        else if (value < 0)
        {
            failures.Add($"{path}: '{field}' must not be negative.");
        }
    }

    private static void RequireNonNegativeInt(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            failures.Add($"{path}: '{field}' must be an integer.");
        }
        else if (value < 0)
        {
            failures.Add($"{path}: '{field}' must not be negative.");
        }
    }

    private static void RequirePositiveInt(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            failures.Add($"{path}: '{field}' must be an integer.");
        }
        else if (value <= 0)
        {
            failures.Add($"{path}: '{field}' must be greater than zero.");
        }
    }

    private static void RequireBool(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False))
        {
            failures.Add($"{path}: '{field}' must be a boolean.");
        }
    }

    // Delegates to JsonElement.TryGetDateTimeOffset rather than DateTimeOffset.TryParse — that's
    // the exact same parser System.Text.Json's own built-in DateTimeOffset converter uses on the
    // final Deserialize call below, so a value this accepts can never then fail to deserialize
    // (Code Review, Story 7.2 Pass 1: DateTimeOffset.TryParse was materially more permissive than
    // STJ's own strict ISO-8601 profile, letting some non-compliant-but-TryParse-able strings pass
    // here and then throw on Deserialize).
    private static void RequireDateTimeOffset(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String || !element.TryGetDateTimeOffset(out _))
        {
            failures.Add($"{path}: '{field}' must be an ISO 8601 timestamp.");
        }
    }

    private static void RequireOptionalDateTimeOffset(JsonElement obj, string field, string path, List<string> failures)
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String || !element.TryGetDateTimeOffset(out _))
        {
            failures.Add($"{path}: '{field}' must be an ISO 8601 timestamp or null.");
        }
    }

    // Enum.TryParse succeeds for ANY numeric string regardless of whether that value is a defined
    // enum member (e.g. "42" parses to an undefined Status) — Enum.IsDefined closes that gap (Code
    // Review, Story 7.2 Pass 1).
    private static void RequireEnum<TEnum>(JsonElement obj, string field, string path, List<string> failures) where TEnum : struct, Enum
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String
            || !Enum.TryParse<TEnum>(element.GetString(), ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            failures.Add(
                $"{path}: '{field}' must be one of [{string.Join(", ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()))}], " +
                $"got '{DescribeForMessage(element)}'.");
        }
    }

    private static void RequireOptionalEnum<TEnum>(JsonElement obj, string field, string path, List<string> failures) where TEnum : struct, Enum
    {
        if (!obj.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.String
            || !Enum.TryParse<TEnum>(element.GetString(), ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            failures.Add(
                $"{path}: '{field}' must be one of [{string.Join(", ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()))}] or null, " +
                $"got '{DescribeForMessage(element)}'.");
        }
    }

    private static string DescribeForMessage(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? "(missing)" : element.GetRawText();
}
