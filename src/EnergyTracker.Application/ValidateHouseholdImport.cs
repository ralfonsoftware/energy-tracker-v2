using System.Text.Json;
using EnergyTracker.Domain;

namespace EnergyTracker.Application;

public record HouseholdImportValidationResult(bool IsValid, IReadOnlyList<string> Failures, HouseholdExportResult? Data)
{
    public static HouseholdImportValidationResult Valid(HouseholdExportResult data) => new(true, [], data);

    public static HouseholdImportValidationResult Invalid(IReadOnlyList<string> failures) => new(false, failures, null);
}

/// <summary>Structurally validates an uploaded v2 export document before any DB write happens (AC #1, #2, #3) — collects every failure instead of failing fast; performs no DB write of any kind, satisfying "never partially applied" structurally.</summary>
public class ValidateHouseholdImport
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public HouseholdImportValidationResult Execute(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return HouseholdImportValidationResult.Invalid([$"The file is not valid JSON: {ex.Message}"]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return HouseholdImportValidationResult.Invalid(["The file's top level must be a JSON object."]);
            }

            // AC #3: v2-to-v2 only — no v1 read/convert path of any kind. Checked first and
            // exclusively when it fails: a genuine v1 file has none of the fields validated below,
            // so reporting a page of unrelated "missing field" failures on top of the real problem
            // would bury it.
            var formatVersion = root.TryGetProperty("formatVersion", out var formatVersionElement) && formatVersionElement.ValueKind == JsonValueKind.String
                ? formatVersionElement.GetString()
                : null;
            if (formatVersion != ExportHouseholdData.FormatVersion)
            {
                return HouseholdImportValidationResult.Invalid(
                    [$"Unsupported formatVersion '{formatVersion ?? "(missing)"}'. Only '{ExportHouseholdData.FormatVersion}' is accepted."]);
            }

            var failures = new List<string>();
            RequireDateTimeOffset(root, "exportedAtUtc", "(root)", failures);
            ValidateHouseholdSettings(root, failures);
            ValidateArray(root, "householdMembers", failures, ValidateHouseholdMember);

            var mainMeterId = ValidateMainMeter(root, failures);

            ValidateArray(root, "meterReadings", failures, (item, path, f) => ValidateMeterReading(item, path, f, mainMeterId));
            var meterReadingIds = CollectIds(root, "meterReadings");

            ValidateArray(root, "meterRegressionPrompts", failures,
                (item, path, f) => ValidateMeterRegressionPrompt(item, path, f, mainMeterId, meterReadingIds));

            ValidateArray(root, "tariffs", failures, ValidateTariff);
            ValidateArray(root, "events", failures, ValidateEvent);
            ValidateArray(root, "rooms", failures, ValidateRoom);
            var roomIds = CollectIds(root, "rooms");

            ValidateArray(root, "powerPoints", failures, (item, path, f) => ValidatePowerPoint(item, path, f, roomIds));
            var powerPointIds = CollectIds(root, "powerPoints");

            ValidateArray(root, "devices", failures, (item, path, f) => ValidateDevice(item, path, f, powerPointIds));
            ValidateArray(root, "smartPlugReadings", failures, (item, path, f) => ValidateSmartPlugReading(item, path, f, powerPointIds));
            ValidateArray(root, "statusSnapshots", failures, ValidateStatusSnapshot);
            ValidateArray(root, "auditCorrections", failures, ValidateAuditCorrection);

            if (failures.Count > 0)
            {
                return HouseholdImportValidationResult.Invalid(failures);
            }

            // Every field this touches has already been shape-, range-, reference- and
            // enum-membership-checked above — still wrapped defensively rather than trusted blindly,
            // since System.Text.Json's exact parsing rules aren't byte-for-byte re-derived by hand
            // above (Code Review, Story 7.2 Pass 1: this used to be an unguarded call that could
            // throw on a technically-valid-per-the-checks-above-but-STJ-rejects value and surface
            // as an unhandled exception instead of a reported failure).
            try
            {
                var data = JsonSerializer.Deserialize<HouseholdExportResult>(json, SerializerOptions)
                    ?? throw new InvalidOperationException("Deserialized to null after passing structural validation.");
                return HouseholdImportValidationResult.Valid(data);
            }
            catch (JsonException ex)
            {
                return HouseholdImportValidationResult.Invalid(
                    [$"The file passed structural validation but could not be parsed: {ex.Message}"]);
            }
        }
    }

    private static void ValidateHouseholdSettings(JsonElement root, List<string> failures)
    {
        if (!root.TryGetProperty("household", out var household) || household.ValueKind != JsonValueKind.Object)
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
    }

    private static Guid? ValidateMainMeter(JsonElement root, List<string> failures)
    {
        if (!root.TryGetProperty("mainMeter", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            failures.Add("'mainMeter' must be an object or null.");
            return null;
        }

        const string path = "mainMeter";
        RequireGuid(element, "id", path, failures);
        RequireDateTimeOffset(element, "createdAtUtc", path, failures);
        RequireOptionalNonNegativeDecimal(element, "digitCapacityKwh", path, failures);

        return TryGetGuid(element, "id");
    }

    private static void ValidateHouseholdMember(JsonElement item, string path, List<string> failures)
    {
        RequireGuid(item, "id", path, failures);
        RequireString(item, "externalIssuer", path, failures);
        RequireString(item, "externalSubjectId", path, failures);
        RequireOptionalString(item, "displayName", path, failures);
        RequireDateTimeOffset(item, "createdAtUtc", path, failures);
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
        RequireString(item, "deviceName", path, failures);
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

    private static void ValidateArray(JsonElement root, string field, List<string> failures, Action<JsonElement, string, List<string>> validateItem)
    {
        if (!root.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            failures.Add($"'{field}' must be an array.");
            return;
        }

        var index = 0;
        var seenIds = new HashSet<Guid>();
        foreach (var item in element.EnumerateArray())
        {
            var path = $"{field}[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                failures.Add($"{path}: must be an object.");
            }
            else
            {
                validateItem(item, path, failures);

                // Every entity category here carries its own "id" — a duplicate collides on the
                // DB primary key during HouseholdRestoreWriter.ChunkedInsertAsync, which reports it
                // as an opaque DB exception rather than a reported validation failure. Checked
                // generically here rather than per-entity-type, since every Validate* method
                // already requires an "id" field the same way.
                if (TryGetGuid(item, "id") is { } id && !seenIds.Add(id))
                {
                    failures.Add($"{path}: 'id' {id} is a duplicate of another entry in '{field}'.");
                }
            }

            index++;
        }
    }

    // Collects the ids of every well-formed item in an array field, for cross-entity reference
    // checks below — an item with a missing/malformed id is silently skipped here (ValidateArray's
    // own RequireGuid check on that same field already reports it as its own failure).
    private static HashSet<Guid> CollectIds(JsonElement root, string field)
    {
        var ids = new HashSet<Guid>();
        if (root.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && TryGetGuid(item, "id") is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
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
