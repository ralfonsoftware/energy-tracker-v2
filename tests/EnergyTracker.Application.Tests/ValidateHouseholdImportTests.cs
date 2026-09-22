using System.Text.Json;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ValidateHouseholdImportTests
{
    private readonly ValidateHouseholdImport _sut = new();

    private static JsonObjectBuilder ValidDocument() => new();

    // Minimal, structurally complete v2 document builder — every test starts from this and
    // mutates only the field(s) it cares about, so a failing assertion always points at a
    // deliberate, single change.
    private sealed class JsonObjectBuilder
    {
        private readonly Dictionary<string, object?> _fields;

        public JsonObjectBuilder()
        {
            _fields = new Dictionary<string, object?>
            {
                ["formatVersion"] = "v2",
                ["exportedAtUtc"] = "2026-09-22T10:00:00+00:00",
                ["household"] = new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["createdAtUtc"] = "2026-01-05T08:00:00+00:00",
                    ["locale"] = "de-DE",
                    ["currency"] = "EUR",
                    ["yearlyBaselineKwh"] = 3500.0,
                    ["trendingThresholdKwh"] = 100.0,
                    ["lowConfidenceGapDays"] = 45,
                    ["tariffCheckCadenceMonths"] = 3,
                    ["aiPlausibilityEnabled"] = false,
                    ["aiPlausibilityBackendConfigured"] = true,
                    ["aiPlausibilityBackendLabel"] = "Local (LMStudio)",
                },
                ["householdMembers"] = new List<object?>(),
                ["mainMeter"] = null,
                ["meterReadings"] = new List<object?>(),
                ["meterRegressionPrompts"] = new List<object?>(),
                ["tariffs"] = new List<object?>(),
                ["events"] = new List<object?>(),
                ["rooms"] = new List<object?>(),
                ["powerPoints"] = new List<object?>(),
                ["devices"] = new List<object?>(),
                ["smartPlugReadings"] = new List<object?>(),
                ["statusSnapshots"] = new List<object?>(),
                ["auditCorrections"] = new List<object?>(),
            };
        }

        public JsonObjectBuilder With(string field, object? value)
        {
            _fields[field] = value;
            return this;
        }

        public JsonObjectBuilder WithHouseholdField(string field, object? value)
        {
            ((Dictionary<string, object?>)_fields["household"]!)[field] = value;
            return this;
        }

        public JsonObjectBuilder Without(string field)
        {
            _fields.Remove(field);
            return this;
        }

        public JsonObjectBuilder WithoutHouseholdField(string field)
        {
            ((Dictionary<string, object?>)_fields["household"]!).Remove(field);
            return this;
        }

        public string Build() => JsonSerializer.Serialize(_fields);
    }

    [Fact]
    public void A_structurally_complete_document_is_valid()
    {
        var result = _sut.Execute(ValidDocument().Build());

        result.IsValid.ShouldBeTrue();
        result.Failures.ShouldBeEmpty();
        result.Data.ShouldNotBeNull();
        result.Data!.FormatVersion.ShouldBe("v2");
    }

    [Fact]
    public void Malformed_JSON_is_reported_as_a_single_failure()
    {
        var result = _sut.Execute("{ not valid json");

        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Data.ShouldBeNull();
    }

    [Fact]
    public void A_JSON_array_at_the_top_level_is_rejected()
    {
        var result = _sut.Execute("[]");

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("top level must be a JSON object"));
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("V2")]
    [InlineData("")]
    public void A_non_v2_formatVersion_is_rejected_alone_v1_to_v1_only_never_converted(string formatVersion)
    {
        var result = _sut.Execute(ValidDocument().With("formatVersion", formatVersion).Build());

        result.IsValid.ShouldBeFalse();
        // Reported alone — no other field's shape is checked once formatVersion itself is wrong.
        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldContain("formatVersion");
    }

    [Fact]
    public void A_missing_formatVersion_is_reported_as_missing_not_a_generic_type_error()
    {
        var result = _sut.Execute(ValidDocument().Without("formatVersion").Build());

        result.IsValid.ShouldBeFalse();
        result.Failures.Single().ShouldContain("(missing)");
    }

    [Fact]
    public void Collects_every_structural_failure_instead_of_failing_fast()
    {
        var document = ValidDocument()
            .WithHouseholdField("locale", 123) // wrong type
            .With("rooms", new List<object?> { new Dictionary<string, object?> { ["id"] = "not-a-guid" } }) // missing name/createdAtUtc, bad id
            .Build();

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeFalse();
        // At least: household.locale wrong type, rooms[0].id bad guid, rooms[0].name missing,
        // rooms[0].createdAtUtc missing — proves multiple independent failures all surface together.
        result.Failures.Count.ShouldBeGreaterThanOrEqualTo(4);
        result.Failures.ShouldContain(f => f.Contains("household") && f.Contains("locale"));
        result.Failures.ShouldContain(f => f.Contains("rooms[0]") && f.Contains("id"));
        result.Failures.ShouldContain(f => f.Contains("rooms[0]") && f.Contains("name"));
        result.Failures.ShouldContain(f => f.Contains("rooms[0]") && f.Contains("createdAtUtc"));
    }

    [Fact]
    public void A_missing_required_field_is_reported()
    {
        var result = _sut.Execute(ValidDocument().WithoutHouseholdField("currency").Build());

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("household") && f.Contains("currency"));
    }

    [Fact]
    public void A_wrong_JSON_type_for_a_field_is_reported()
    {
        var result = _sut.Execute(ValidDocument().WithHouseholdField("trendingThresholdKwh", "one hundred").Build());

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("trendingThresholdKwh"));
    }

    [Fact]
    public void An_unparseable_MeterRegressionPrompt_classification_string_is_reported_never_silently_defaulted()
    {
        var prompt = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["mainMeterId"] = Guid.NewGuid().ToString(),
            ["meterReadingId"] = Guid.NewGuid().ToString(),
            ["previousMeterReadingId"] = Guid.NewGuid().ToString(),
            ["createdAtUtc"] = "2026-01-05T08:00:00+00:00",
            ["resolvedAtUtc"] = null,
            ["classification"] = "Unknown",
            ["digitCapacityKwh"] = null,
        };
        var document = ValidDocument().With("meterRegressionPrompts", new List<object?> { prompt }).Build();

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("classification") && f.Contains("reset") && f.Contains("rollover"));
    }

    [Theory]
    [InlineData("Reset")]
    [InlineData("ROLLOVER")]
    [InlineData("reset")]
    public void MeterRegressionPrompt_classification_accepts_the_documented_names_case_insensitively(string classification)
    {
        var mainMeterId = Guid.NewGuid();
        var previousReadingId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        var mainMeter = new Dictionary<string, object?>
        {
            ["id"] = mainMeterId.ToString(),
            ["createdAtUtc"] = "2026-01-01T00:00:00+00:00",
            ["digitCapacityKwh"] = null,
        };
        var readings = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["id"] = previousReadingId.ToString(),
                ["mainMeterId"] = mainMeterId.ToString(),
                ["kwhValue"] = 90.0,
                ["readingTimestamp"] = "2026-01-04T08:00:00+00:00",
                ["idempotencyKey"] = Guid.NewGuid().ToString(),
                ["createdAtUtc"] = "2026-01-04T08:00:00+00:00",
            },
            new Dictionary<string, object?>
            {
                ["id"] = readingId.ToString(),
                ["mainMeterId"] = mainMeterId.ToString(),
                ["kwhValue"] = 10.0,
                ["readingTimestamp"] = "2026-01-05T08:00:00+00:00",
                ["idempotencyKey"] = Guid.NewGuid().ToString(),
                ["createdAtUtc"] = "2026-01-05T08:00:00+00:00",
            },
        };
        var prompt = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["mainMeterId"] = mainMeterId.ToString(),
            ["meterReadingId"] = readingId.ToString(),
            ["previousMeterReadingId"] = previousReadingId.ToString(),
            ["createdAtUtc"] = "2026-01-05T08:00:00+00:00",
            ["resolvedAtUtc"] = null,
            ["classification"] = classification,
            ["digitCapacityKwh"] = null,
        };
        var document = ValidDocument()
            .With("mainMeter", mainMeter)
            .With("meterReadings", readings)
            .With("meterRegressionPrompts", new List<object?> { prompt })
            .Build();

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void An_unparseable_StatusSnapshot_status_string_is_reported()
    {
        var snapshot = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["status"] = "SomethingElse",
            ["paceToDateKwh"] = 10.0,
            ["baselineToDateKwh"] = 12.0,
            ["isLowConfidence"] = false,
            ["computedAtUtc"] = "2026-09-01T00:00:00+00:00",
        };
        var document = ValidDocument().With("statusSnapshots", new List<object?> { snapshot }).Build();

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("status") && f.Contains("withinrange"));
    }

    // Code review, Story 7.2 Pass 1: Enum.TryParse accepts any numeric string regardless of
    // whether it's a defined enum member — "42" would previously pass structural validation and
    // get written straight into the DB as an undefined Status value. Caught live during the
    // Pass-1/2 live re-verification session before being covered by an automated test.
    [Fact]
    public void An_undefined_numeric_StatusSnapshot_status_value_is_reported_not_silently_accepted()
    {
        var snapshot = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["status"] = "42",
            ["paceToDateKwh"] = 10.0,
            ["baselineToDateKwh"] = 12.0,
            ["isLowConfidence"] = false,
            ["computedAtUtc"] = "2026-09-01T00:00:00+00:00",
        };
        var document = ValidDocument().With("statusSnapshots", new List<object?> { snapshot }).Build();

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("status") && f.Contains("withinrange"));
    }

    // Code review, Story 7.2 Pass 1: a duplicate id within one entity array passes structural
    // validation and only fails deep inside HouseholdRestoreWriter as a raw DB PK-violation
    // exception with no diagnostic value forwarded to the user.
    [Fact]
    public void A_duplicate_id_within_the_same_entity_array_is_reported()
    {
        var room = new Dictionary<string, object?>
        {
            ["id"] = "11111111-1111-1111-1111-111111111111",
            ["name"] = "Kitchen",
            ["createdAtUtc"] = "2026-01-05T08:00:00+00:00",
            ["archivedAt"] = null,
        };
        var document = ValidDocument()
            .With("rooms", new List<object?> { room, new Dictionary<string, object?>(room) })
            .Build();

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("rooms[1]") && f.Contains("duplicate"));
    }

    // Code review, Story 7.2 Pass 1: an FK-like field (roomId) that doesn't resolve to any id
    // present elsewhere in the file passes structural validation and only fails deep inside
    // HouseholdRestoreWriter as a raw DB FK-violation exception.
    [Fact]
    public void A_powerPoint_roomId_that_does_not_match_any_room_in_the_file_is_reported()
    {
        var powerPoint = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["roomId"] = Guid.NewGuid().ToString(), // no room in the document has this id
            ["name"] = "Outlet",
            ["createdAtUtc"] = "2026-01-05T08:00:00+00:00",
            ["archivedAt"] = null,
        };
        var document = ValidDocument().With("powerPoints", new List<object?> { powerPoint }).Build();

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("powerPoints[0]") && f.Contains("roomId") && f.Contains("does not match"));
    }

    // Code review, Story 7.2 Pass 1 (resolved with Ralf): a structurally valid but semantically
    // nonsensical migration file must not silently overwrite live household data with garbage —
    // caught live during the Pass-1/2 live re-verification session, where MeterReading.kwhValue
    // was found to have been missed by the original patch (still plain RequireDecimal, allowing
    // negative values) despite SmartPlugReading's identical field having been fixed correctly.
    [Fact]
    public void A_negative_MeterReading_kwhValue_is_reported()
    {
        var reading = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["mainMeterId"] = Guid.NewGuid().ToString(),
            ["kwhValue"] = -50.0,
            ["readingTimestamp"] = "2026-09-01T00:00:00+00:00",
            ["idempotencyKey"] = Guid.NewGuid().ToString(),
            ["createdAtUtc"] = "2026-09-01T00:00:00+00:00",
        };
        var document = ValidDocument().With("meterReadings", new List<object?> { reading }).Build();

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("meterReadings[0]") && f.Contains("kwhValue") && f.Contains("negative"));
    }

    [Fact]
    public void A_missing_array_field_is_reported_not_treated_as_empty()
    {
        var result = _sut.Execute(ValidDocument().Without("meterReadings").Build());

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("meterReadings"));
    }

    [Fact]
    public void Null_mainMeter_is_valid()
    {
        var result = _sut.Execute(ValidDocument().With("mainMeter", null).Build());

        result.IsValid.ShouldBeTrue();
        result.Data!.MainMeter.ShouldBeNull();
    }

    [Fact]
    public void A_populated_document_with_every_entity_category_present_is_valid_and_deserializes()
    {
        var householdId = Guid.NewGuid();
        var mainMeterId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var powerPointId = Guid.NewGuid();

        var document = ValidDocument()
            .With("mainMeter", new Dictionary<string, object?>
            {
                ["id"] = mainMeterId.ToString(), ["createdAtUtc"] = "2026-01-05T09:00:00+00:00", ["digitCapacityKwh"] = null,
            })
            .With("rooms", new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = roomId.ToString(), ["name"] = "Kitchen", ["createdAtUtc"] = "2026-01-05T08:15:00+00:00", ["archivedAt"] = null,
                },
            })
            .With("powerPoints", new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = powerPointId.ToString(), ["roomId"] = roomId.ToString(), ["name"] = "Outlet",
                    ["createdAtUtc"] = "2026-01-05T08:16:00+00:00", ["archivedAt"] = null,
                },
            })
            .With("smartPlugReadings", new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid().ToString(), ["powerPointId"] = powerPointId.ToString(), ["roomName"] = "Kitchen",
                    ["powerPointName"] = "Outlet", ["deviceName"] = "Kettle", ["intervalStart"] = "2026-09-01T00:00:00+00:00",
                    ["intervalEnd"] = "2026-09-01T00:15:00+00:00", ["kwhValue"] = 0.5,
                },
            })
            .Build();
        _ = householdId;

        var result = _sut.Execute(document);

        result.IsValid.ShouldBeTrue();
        result.Data!.MainMeter!.Id.ShouldBe(mainMeterId);
        result.Data.Rooms.Single().Id.ShouldBe(roomId);
        result.Data.PowerPoints.Single().RoomId.ShouldBe(roomId);
        result.Data.SmartPlugReadings.Single().PowerPointId.ShouldBe(powerPointId);
    }
}
