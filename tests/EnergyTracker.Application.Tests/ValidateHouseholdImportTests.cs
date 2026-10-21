using System.Text;
using System.Text.Json;
using EnergyTracker.Domain;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ValidateHouseholdImportTests
{
    private readonly ValidateHouseholdImport _sut = new();

    // Story 11.10: the validator reads a seekable stream, never a string. Every pre-existing test goes
    // through this helper unchanged otherwise, so the old assertions stay the oracle for the failure
    // text and order.
    private HouseholdImportValidationResult Validate(string json) => Validate(Encoding.UTF8.GetBytes(json));

    private HouseholdImportValidationResult Validate(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return _sut.Execute(stream);
    }

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

        // Same document with the named fields written first, in that order: the validator must not
        // depend on the file's own property order (a reference may point at an array further down).
        public string BuildWithFieldsFirst(params string[] leading)
        {
            var names = leading.Where(_fields.ContainsKey).Concat(_fields.Keys.Except(leading));
            return "{" + string.Join(",", names.Select(n => $"{JsonSerializer.Serialize(n)}:{JsonSerializer.Serialize(_fields[n])}")) + "}";
        }
    }

    [Fact]
    public void A_structurally_complete_document_is_valid()
    {
        var result = Validate(ValidDocument().Build());

        result.IsValid.ShouldBeTrue();
        result.Failures.ShouldBeEmpty();
        result.Inventory.ShouldNotBeNull();
    }

    [Fact]
    public void Malformed_JSON_is_reported_as_a_single_failure()
    {
        var result = Validate("{ not valid json");

        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Inventory.ShouldBeNull();
    }

    [Fact]
    public void A_JSON_array_at_the_top_level_is_rejected()
    {
        var result = Validate("[]");

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("top level must be a JSON object"));
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("V2")]
    [InlineData("")]
    public void A_non_v2_formatVersion_is_rejected_alone_v1_to_v1_only_never_converted(string formatVersion)
    {
        var result = Validate(ValidDocument().With("formatVersion", formatVersion).Build());

        result.IsValid.ShouldBeFalse();
        // Reported alone — no other field's shape is checked once formatVersion itself is wrong.
        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldContain("formatVersion");
    }

    [Fact]
    public void A_missing_formatVersion_is_reported_as_missing_not_a_generic_type_error()
    {
        var result = Validate(ValidDocument().Without("formatVersion").Build());

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

        var result = Validate(document);

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
        var result = Validate(ValidDocument().WithoutHouseholdField("currency").Build());

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("household") && f.Contains("currency"));
    }

    [Fact]
    public void A_wrong_JSON_type_for_a_field_is_reported()
    {
        var result = Validate(ValidDocument().WithHouseholdField("trendingThresholdKwh", "one hundred").Build());

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

        var result = Validate(document);

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

        var result = Validate(document);

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

        var result = Validate(document);

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

        var result = Validate(document);

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

        var result = Validate(document);

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

        var result = Validate(document);

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

        var result = Validate(document);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("meterReadings[0]") && f.Contains("kwhValue") && f.Contains("negative"));
    }

    // Story 11.4, AD-20: DeviceName is part of the unmapped-reading key and the column is bounded, so a
    // restore must reject an over-long value up front instead of failing mid-write.
    private static Dictionary<string, object?> SmartPlugReadingWithDeviceName(string deviceName) => new()
    {
        ["id"] = Guid.NewGuid().ToString(), ["powerPointId"] = null, ["roomName"] = "Kitchen",
        ["powerPointName"] = "Outlet", ["deviceName"] = deviceName, ["intervalStart"] = "2026-09-01T00:00:00+00:00",
        ["intervalEnd"] = "2026-09-01T00:15:00+00:00", ["kwhValue"] = 0.5,
    };

    [Fact]
    public void A_smartPlugReading_deviceName_of_exactly_the_maximum_length_is_valid()
    {
        var document = ValidDocument()
            .With("smartPlugReadings", new List<object?> { SmartPlugReadingWithDeviceName(new string('a', SmartPlugReading.DeviceNameMaxLength)) })
            .Build();

        var result = Validate(document);

        result.Failures.ShouldBeEmpty();
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_smartPlugReading_deviceName_over_the_maximum_length_is_reported_with_its_path_field_and_limit()
    {
        var document = ValidDocument()
            .With("smartPlugReadings", new List<object?>
            {
                SmartPlugReadingWithDeviceName("Fridge"),
                SmartPlugReadingWithDeviceName(new string('a', SmartPlugReading.DeviceNameMaxLength + 1)),
            })
            .Build();

        var result = Validate(document);

        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldBe("smartPlugReadings[1]: 'deviceName' must be at most 200 characters.");
    }

    [Fact]
    public void A_missing_array_field_is_reported_not_treated_as_empty()
    {
        var result = Validate(ValidDocument().Without("meterReadings").Build());

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("meterReadings"));
    }

    [Fact]
    public void Null_mainMeter_is_valid()
    {
        var result = Validate(ValidDocument().With("mainMeter", null).Build());

        result.IsValid.ShouldBeTrue();
        result.Inventory!.HasMainMeter.ShouldBeFalse();
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

        var result = Validate(document);

        result.IsValid.ShouldBeTrue();
        result.Inventory!.HasMainMeter.ShouldBeTrue();
        result.Inventory.Rooms.ShouldBe(1);
        result.Inventory.PowerPoints.ShouldBe(1);
        result.Inventory.SmartPlugReadings.ShouldBe(1);
    }

    // --- householdMembers (AD-25 / audit S1, S12): the validator needs only `id`; `displayName` is
    // optional; the legacy identity fields of pre-change exports are accepted and ignored. ---

    private static Dictionary<string, object?> Member(Action<Dictionary<string, object?>>? mutate = null)
    {
        var member = new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["displayName"] = "Ralf" };
        mutate?.Invoke(member);
        return member;
    }

    private HouseholdImportValidationResult ValidateWithMember(Dictionary<string, object?> member) =>
        Validate(ValidDocument().With("householdMembers", new List<object?> { member }).Build());

    [Fact]
    public void A_member_in_the_new_id_and_displayName_shape_is_valid()
    {
        var member = Member();

        var result = ValidateWithMember(member);

        result.IsValid.ShouldBeTrue();
        result.Inventory!.HouseholdMembers.ShouldBe(1);
        result.Inventory.MemberDisplayNames.ShouldBe(["Ralf"]);
    }

    [Fact]
    public void A_member_with_only_an_id_is_valid_and_has_a_null_DisplayName()
    {
        var member = new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString() };

        var result = ValidateWithMember(member);

        result.IsValid.ShouldBeTrue();
        result.Inventory!.HouseholdMembers.ShouldBe(1);
        result.Inventory.MemberDisplayNames.ShouldBeEmpty();
    }

    [Fact]
    public void A_member_with_a_null_displayName_is_valid()
    {
        var result = ValidateWithMember(Member(m => m["displayName"] = null));

        result.IsValid.ShouldBeTrue();
        result.Inventory!.HouseholdMembers.ShouldBe(1);
        result.Inventory.MemberDisplayNames.ShouldBeEmpty();
    }

    // Verbatim pre-change member shape: such files exist in the wild (Story 7.1 exports) and must
    // still restore. The identity fields are deliberately not validated at all.
    [Fact]
    public void A_legacy_member_carrying_identity_fields_is_valid_and_the_identity_fields_are_ignored()
    {
        var member = Member(m =>
        {
            m["externalIssuer"] = "https://tenant.eu.auth0.com/";
            m["externalSubjectId"] = "auth0|abc123";
            m["createdAtUtc"] = "2026-01-05T08:00:00+00:00";
        });

        var result = ValidateWithMember(member);

        result.IsValid.ShouldBeTrue();
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public void Legacy_identity_fields_of_any_JSON_type_are_ignored_not_validated()
    {
        var member = Member(m =>
        {
            m["externalIssuer"] = 42;
            m["externalSubjectId"] = new List<object?>();
            m["createdAtUtc"] = "not a date";
        });

        var result = ValidateWithMember(member);

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-a-guid")]
    public void A_member_without_a_valid_id_is_rejected(string idCase)
    {
        var member = Member(m =>
        {
            if (idCase == "missing")
            {
                m.Remove("id");
            }
            else
            {
                m["id"] = idCase;
            }
        });

        var result = ValidateWithMember(member);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain(f => f.Contains("householdMembers[0]") && f.Contains("'id'"));
    }

    [Fact]
    public void A_non_string_displayName_is_rejected()
    {
        var result = ValidateWithMember(Member(m => m["displayName"] = 5));

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain("householdMembers[0]: 'displayName' must be a string or null.");
    }

    // --- Story 11.10 (audit S4): streaming behaviours that must not change what the whole-document
    // validator did. Each is a trap of validating one array element at a time. ---

    // AC #6a: malformed JSON at the very end yields the single not-valid-JSON failure and none of the
    // structural failures collected before it.
    [Fact]
    public void Malformed_JSON_at_the_end_is_the_single_failure_with_no_partial_structural_failures()
    {
        var broken = ValidDocument().WithHouseholdField("locale", 123).With("rooms", new List<object?> { new Dictionary<string, object?> { ["id"] = "x" } }).Build();
        var json = broken[..^1] + ",\"tail\":[1,2,}";

        var result = Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldStartWith("The file is not valid JSON:");
    }

    // AC #6b: formatVersion is checked first and alone even when it is the last property of the file.
    [Fact]
    public void A_wrong_formatVersion_as_the_last_property_is_reported_alone()
    {
        var withoutVersion = ValidDocument().WithHouseholdField("locale", 123).Without("formatVersion").Build();
        var json = withoutVersion[..^1] + ",\"formatVersion\":\"v1\"}";

        var result = Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldContain("Unsupported formatVersion 'v1'");
    }

    [Fact]
    public void A_valid_formatVersion_as_the_last_property_is_accepted()
    {
        var withoutVersion = ValidDocument().Without("formatVersion").Build();

        Validate(withoutVersion[..^1] + ",\"formatVersion\":\"v2\"}").IsValid.ShouldBeTrue();
    }

    private JsonObjectBuilder DocumentWithEveryReferenceKind(out Guid roomId)
    {
        var mainMeterId = Guid.NewGuid();
        var previousReadingId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        roomId = Guid.NewGuid();
        var powerPointId = Guid.NewGuid();
        Dictionary<string, object?> Reading(Guid id, double kwh, string at) => new()
        {
            ["id"] = id.ToString(), ["mainMeterId"] = mainMeterId.ToString(), ["kwhValue"] = kwh, ["readingTimestamp"] = at,
            ["idempotencyKey"] = Guid.NewGuid().ToString(), ["createdAtUtc"] = at,
        };

        return ValidDocument()
            .With("mainMeter", new Dictionary<string, object?> { ["id"] = mainMeterId.ToString(), ["createdAtUtc"] = "2026-01-01T00:00:00+00:00", ["digitCapacityKwh"] = null })
            .With("meterReadings", new List<object?> { Reading(previousReadingId, 90, "2026-01-04T08:00:00+00:00"), Reading(readingId, 10, "2026-01-05T08:00:00+00:00") })
            .With("meterRegressionPrompts", new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid().ToString(), ["mainMeterId"] = mainMeterId.ToString(), ["meterReadingId"] = readingId.ToString(),
                    ["previousMeterReadingId"] = previousReadingId.ToString(), ["createdAtUtc"] = "2026-01-05T08:00:00+00:00",
                    ["resolvedAtUtc"] = null, ["classification"] = "reset", ["digitCapacityKwh"] = null,
                },
            })
            .With("rooms", new List<object?> { new Dictionary<string, object?> { ["id"] = roomId.ToString(), ["name"] = "Kitchen", ["createdAtUtc"] = "2026-01-05T08:15:00+00:00", ["archivedAt"] = null } })
            .With("powerPoints", new List<object?>
            {
                new Dictionary<string, object?> { ["id"] = powerPointId.ToString(), ["roomId"] = roomId.ToString(), ["name"] = "Outlet", ["createdAtUtc"] = "2026-01-05T08:16:00+00:00", ["archivedAt"] = null },
            })
            .With("devices", new List<object?>
            {
                new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["powerPointId"] = powerPointId.ToString(), ["name"] = "Kettle", ["createdAtUtc"] = "2026-01-05T08:17:00+00:00", ["archivedAt"] = null },
            })
            .With("smartPlugReadings", new List<object?> { SmartPlugReadingFor(powerPointId) });
    }

    private static Dictionary<string, object?> SmartPlugReadingFor(Guid powerPointId) => new()
    {
        ["id"] = Guid.NewGuid().ToString(), ["powerPointId"] = powerPointId.ToString(), ["roomName"] = "Kitchen",
        ["powerPointName"] = "Outlet", ["deviceName"] = "Kettle", ["intervalStart"] = "2026-09-01T00:00:00+00:00",
        ["intervalEnd"] = "2026-09-01T00:15:00+00:00", ["kwhValue"] = 0.5,
    };

    // AC #6c: a reference to an array that appears LATER in the file still resolves, for every kind of
    // reference (mainMeterId, meterReadingId/previousMeterReadingId, roomId, powerPointId).
    [Fact]
    public void References_to_arrays_that_appear_later_in_the_file_still_resolve()
    {
        var json = DocumentWithEveryReferenceKind(out _).BuildWithFieldsFirst(
            "devices", "smartPlugReadings", "powerPoints", "meterRegressionPrompts", "meterReadings", "rooms", "mainMeter");

        var result = Validate(json);

        result.Failures.ShouldBeEmpty();
        result.IsValid.ShouldBeTrue();
        result.Inventory!.Devices.ShouldBe(1);
        result.Inventory.MeterRegressionPrompts.ShouldBe(1);
    }

    [Fact]
    public void A_reference_to_nothing_is_still_reported_when_the_target_array_comes_later()
    {
        var document = DocumentWithEveryReferenceKind(out _);
        document.With("devices", new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid().ToString(), ["powerPointId"] = Guid.NewGuid().ToString(), ["name"] = "Kettle",
                ["createdAtUtc"] = "2026-01-05T08:17:00+00:00", ["archivedAt"] = null,
            },
        });

        var result = Validate(document.BuildWithFieldsFirst("devices", "powerPoints"));

        result.Failures.ShouldContain(f => f.Contains("devices[0]") && f.Contains("powerPointId") && f.Contains("does not match"));
    }

    // AC #6d: a leading UTF-8 BOM is accepted (the old StreamReader stripped it; Utf8JsonReader throws).
    [Fact]
    public void A_leading_UTF8_BOM_is_accepted()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ValidDocument().Build())).ToArray();

        var result = Validate(bytes);

        result.Failures.ShouldBeEmpty();
        result.IsValid.ShouldBeTrue();
    }

    // AC #6e: the legacy identity fields are accepted and ignored; the inventory carries only the count
    // and the display names, never an identity field.
    [Fact]
    public void A_legacy_member_is_valid_and_the_inventory_carries_only_the_count_and_display_name()
    {
        var member = Member(m =>
        {
            m["externalIssuer"] = "https://tenant.eu.auth0.com/";
            m["externalSubjectId"] = "auth0|abc123";
        });

        var result = ValidateWithMember(member);

        result.IsValid.ShouldBeTrue();
        result.Inventory!.HouseholdMembers.ShouldBe(1);
        result.Inventory.MemberDisplayNames.ShouldBe(["Ralf"]);
    }

    // AC #6f: a structurally valid item that System.Text.Json still cannot deserialize is a reported
    // failure now, never a surprise in the restore job. Guid.TryParse accepts the 32-digit "N" format
    // the structural check uses; the Guid converter of System.Text.Json accepts only the hyphenated one.
    [Fact]
    public void A_structurally_valid_item_that_System_Text_Json_rejects_is_a_reported_failure()
    {
        var room = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["name"] = "Kitchen", ["createdAtUtc"] = "2026-01-05T08:15:00+00:00", ["archivedAt"] = null,
        };

        var result = Validate(ValidDocument().With("rooms", new List<object?> { room }).Build());

        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldStartWith("The file passed structural validation but could not be parsed: rooms[0]");
    }

    [Fact]
    public void A_stream_that_cannot_seek_is_refused_never_copied_into_memory()
    {
        using var stream = new NonSeekableStream(Encoding.UTF8.GetBytes(ValidDocument().Build()));

        Should.Throw<ArgumentException>(() => _sut.Execute(stream));
    }

    // AC #7b: the first 200 failures in canonical field order, then one entry with the exact remainder.
    // The household failure sits earlier in the validator's order than the rooms, although the file
    // lists it last; the auditCorrections failure sits after them and is cut.
    [Fact]
    public void More_than_200_failures_keep_the_first_200_in_canonical_order_then_an_exact_count_of_the_rest()
    {
        var rooms = Enumerable.Range(0, 300).Select(_ => (object?)new Dictionary<string, object?> { ["id"] = "not-a-guid" }).ToList();
        var json = ValidDocument()
            .With("rooms", rooms)
            .With("auditCorrections", 5)
            .WithHouseholdField("locale", 123)
            .BuildWithFieldsFirst("rooms", "auditCorrections");

        var result = Validate(json);

        // 1 household + 300 rooms x 3 (id, name, createdAtUtc) + 1 auditCorrections = 902 failures.
        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(201);
        result.Failures[0].ShouldBe("household: 'locale' must be a string.");
        result.Failures[1].ShouldBe("rooms[0]: 'id' must be a GUID string.");
        result.Failures[199].ShouldStartWith("rooms[");
        result.Failures.ShouldNotContain("'auditCorrections' must be an array.");
        result.Failures[200].ShouldBe("… and 702 more failures not shown.");
    }

    [Fact]
    public void Exactly_200_failures_are_all_shown_without_a_remainder_entry()
    {
        var rooms = Enumerable.Range(0, 200).Select(_ => (object?)new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(), ["name"] = "Kitchen", ["createdAtUtc"] = "not a date",
        }).ToList();

        var result = Validate(ValidDocument().With("rooms", rooms).Build());

        result.Failures.Count.ShouldBe(200);
        result.Failures.ShouldNotContain(f => f.Contains("more failures"));
    }

    // AC #7c: the member count stays exact, the retained names are bounded.
    [Fact]
    public void A_thousand_members_retain_100_names_of_at_most_200_characters_while_the_count_stays_exact()
    {
        var members = Enumerable.Range(0, 1000)
            .Select(i => (object?)new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["displayName"] = new string((char)('a' + i % 26), 500) })
            .ToList();

        var result = Validate(ValidDocument().With("householdMembers", members).Build());

        result.IsValid.ShouldBeTrue();
        result.Inventory!.HouseholdMembers.ShouldBe(1000);
        result.Inventory.MemberDisplayNames.Count.ShouldBe(100);
        result.Inventory.MemberDisplayNames.ShouldAllBe(name => name.Length == 200);
    }

    [Fact]
    public void Member_names_are_trimmed_and_blank_names_are_dropped_in_file_order()
    {
        var members = new List<object?>
        {
            new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["displayName"] = "  Ralf " },
            new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["displayName"] = "   " },
            new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["displayName"] = null },
            new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["displayName"] = "Mia" },
        };

        var result = Validate(ValidDocument().With("householdMembers", members).Build());

        result.Inventory!.HouseholdMembers.ShouldBe(4);
        result.Inventory.MemberDisplayNames.ShouldBe(["Ralf", "Mia"]);
    }

    // AC #7a: a single value that does not fit the 4 MiB buffer is a stable failure; it never grows
    // the buffer. A 64 MiB single string costs the reader 4 MiB, not 64.
    [Fact]
    public void A_single_string_value_larger_than_4_MiB_fails_with_the_stable_failure_without_buffering_it()
    {
        using var inner = new PatternStream("{\"formatVersion\":\"v2\",\"huge\":\"", (byte)'a', 64L * 1024 * 1024, "\"}");
        using var stream = new CountingStream(inner);

        var result = _sut.Execute(stream);

        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldContain("larger than 4 MiB");
        stream.LargestReadRequest.ShouldBeLessThanOrEqualTo(4 * 1024 * 1024);
        stream.TotalBytesRead.ShouldBeLessThan(4 * 1024 * 1024 + 64 * 1024);
    }

    [Fact]
    public void A_single_array_element_larger_than_4_MiB_fails_with_the_stable_failure()
    {
        var hugeName = new string('a', 5 * 1024 * 1024);
        var room = new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["name"] = hugeName, ["createdAtUtc"] = "2026-01-05T08:15:00+00:00", ["archivedAt"] = null };
        using var stream = new CountingStream(new MemoryStream(Encoding.UTF8.GetBytes(ValidDocument().With("rooms", new List<object?> { room }).Build())));

        var result = _sut.Execute(stream);

        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldContain("larger than 4 MiB");
        stream.LargestReadRequest.ShouldBeLessThanOrEqualTo(4 * 1024 * 1024);
    }

    // AC #1/#7: the CI guard for bounded buffering. Not a process-wide memory reading: a 33 MB document
    // is validated through a stream that records every read, and the biggest request must stay at the
    // buffer cap while the whole file is read and the result is right. Skip() on the root array, or
    // reading the file into one buffer, would grow the request past the cap (or fail the file).
    [Fact]
    public void A_33_MB_export_is_validated_through_a_bounded_buffer_and_read_in_full()
    {
        const int readings = 130_000;
        var roomId = Guid.NewGuid();
        var powerPointId = Guid.NewGuid();
        var document = new MemoryStream();
        using (var writer = new Utf8JsonWriter(document))
        {
            writer.WriteStartObject();
            writer.WriteString("formatVersion", "v2");
            writer.WriteString("exportedAtUtc", "2026-09-22T10:00:00+00:00");
            writer.WritePropertyName("household");
            JsonSerializer.Serialize(writer, new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid(), ["createdAtUtc"] = "2026-01-05T08:00:00+00:00", ["locale"] = "de-DE", ["currency"] = "EUR",
                ["yearlyBaselineKwh"] = 3500.0, ["trendingThresholdKwh"] = 100.0, ["lowConfidenceGapDays"] = 45,
                ["tariffCheckCadenceMonths"] = 3, ["aiPlausibilityEnabled"] = false, ["aiPlausibilityBackendConfigured"] = false,
                ["aiPlausibilityBackendLabel"] = null,
            });
            foreach (var empty in new[] { "householdMembers", "meterReadings", "meterRegressionPrompts", "tariffs", "events" })
            {
                writer.WriteStartArray(empty);
                writer.WriteEndArray();
            }

            writer.WriteNull("mainMeter");
            writer.WriteStartArray("rooms");
            JsonSerializer.Serialize(writer, new Dictionary<string, object?> { ["id"] = roomId, ["name"] = "Kitchen", ["createdAtUtc"] = "2026-01-05T08:15:00+00:00", ["archivedAt"] = null });
            writer.WriteEndArray();
            writer.WriteStartArray("powerPoints");
            JsonSerializer.Serialize(writer, new Dictionary<string, object?> { ["id"] = powerPointId, ["roomId"] = roomId, ["name"] = "Outlet", ["createdAtUtc"] = "2026-01-05T08:16:00+00:00", ["archivedAt"] = null });
            writer.WriteEndArray();
            writer.WriteStartArray("devices");
            writer.WriteEndArray();
            writer.WriteStartArray("smartPlugReadings");
            for (var i = 0; i < readings; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("id", Guid.NewGuid());
                writer.WriteString("powerPointId", powerPointId);
                writer.WriteString("roomName", "Kitchen");
                writer.WriteString("powerPointName", "Outlet");
                writer.WriteString("deviceName", "Kettle");
                writer.WriteString("intervalStart", "2026-09-01T00:00:00+00:00");
                writer.WriteString("intervalEnd", "2026-09-01T00:10:00+00:00");
                writer.WriteNumber("kwhValue", 0.0125m);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("statusSnapshots");
            writer.WriteEndArray();
            writer.WriteStartArray("auditCorrections");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        document.Length.ShouldBeGreaterThan(33L * 1024 * 1024);
        using var stream = new CountingStream(document);

        var result = _sut.Execute(stream);

        result.Failures.ShouldBeEmpty();
        result.IsValid.ShouldBeTrue();
        result.Inventory!.SmartPlugReadings.ShouldBe(readings);
        result.Inventory.Rooms.ShouldBe(1);
        result.Inventory.PowerPoints.ShouldBe(1);
        // Small elements never grow the buffer past its initial 64 KiB, so no read asks for more (the 4 MiB cap
        // is the hard limit; this also catches a reader that grows for no reason).
        stream.LargestReadRequest.ShouldBeLessThanOrEqualTo(64 * 1024);
        stream.TotalBytesRead.ShouldBeGreaterThanOrEqualTo(document.Length);
    }


    // --- Code review of Story 11.10: findings that were regressions against the whole-document validator. ---

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    [InlineData("true")]
    public void A_mainMeter_that_is_neither_an_object_nor_null_is_reported(string rawValue)
    {
        var json = ValidDocument().Without("mainMeter").Build();
        json = json[..^1] + $",\"mainMeter\":{rawValue}}}";

        var result = Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldBe(["'mainMeter' must be an object or null."]);
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("[1]")]
    [InlineData("5")]
    public void An_exportedAtUtc_that_is_not_a_timestamp_string_is_reported_whatever_its_kind(string rawValue)
    {
        var json = ValidDocument().Without("exportedAtUtc").Build();
        json = json[..^1] + $",\"exportedAtUtc\":{rawValue}}}";

        var result = Validate(json);

        result.Failures.ShouldBe(["(root): 'exportedAtUtc' must be an ISO 8601 timestamp."]);
    }

    // The restore keeps only the last occurrence of a repeated key, so a repeated field is a failure rather
    // than two arrays validated, counted and cross-referenced as if both were restored.
    [Fact]
    public void A_field_that_appears_twice_is_reported_and_not_counted_twice()
    {
        var json = ValidDocument().Build();
        json = json[..^1] + ",\"tariffs\":[]}";

        var result = Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldBe(["'tariffs' appears more than once."]);
    }

    [Fact]
    public void A_reference_that_only_resolves_in_a_repeated_arrays_dropped_copy_is_not_resolved()
    {
        var roomId = Guid.NewGuid();
        var room = new Dictionary<string, object?> { ["id"] = roomId.ToString(), ["name"] = "Kitchen", ["createdAtUtc"] = "2026-01-05T08:15:00+00:00", ["archivedAt"] = null };
        var powerPoint = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(), ["roomId"] = roomId.ToString(), ["name"] = "Outlet", ["createdAtUtc"] = "2026-01-05T08:16:00+00:00", ["archivedAt"] = null,
        };
        // rooms: [room] first, then rooms: [] again. The restore would see the second (empty) one.
        var json = ValidDocument().With("rooms", new List<object?> { room }).With("powerPoints", new List<object?> { powerPoint }).Build();
        json = json[..^1] + ",\"rooms\":[]}";

        var result = Validate(json);

        result.IsValid.ShouldBeFalse();
        result.Failures.ShouldContain("'rooms' appears more than once.");
    }

    // "ZZ" marks where a lone 0xFC (a Latin-1 u-umlaut, not valid UTF-8) goes.
    private HouseholdImportValidationResult ValidateWithInvalidUtf8(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json).ToList();
        var at = bytes.FindIndex(i => i == (byte)'Z');
        while (at >= 0 && !(at + 1 < bytes.Count && bytes[at + 1] == (byte)'Z'))
        {
            at = bytes.FindIndex(at + 1, i => i == (byte)'Z');
        }

        at.ShouldBeGreaterThanOrEqualTo(0, "the ZZ marker is missing from the test document");
        bytes.RemoveRange(at, 2);
        bytes.Insert(at, 0xFC);
        return Validate(bytes.ToArray());
    }

    // The old StreamReader substituted U+FFFD; Utf8JsonReader only throws when the string is read, as an
    // InvalidOperationException. That is a reported 400 failure, never an unhandled 500.
    [Fact]
    public void Invalid_UTF8_in_a_member_name_is_a_reported_failure_not_an_unhandled_exception()
    {
        var members = new List<object?> { new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString(), ["displayName"] = "JZZr" } };

        var result = ValidateWithInvalidUtf8(ValidDocument().With("householdMembers", members).Build());

        result.IsValid.ShouldBeFalse();
        result.Failures.Count.ShouldBe(1);
        result.Failures.Single().ShouldStartWith("The file is not valid JSON:");
    }

    [Fact]
    public void Invalid_UTF8_in_a_root_property_name_is_a_reported_failure_not_an_unhandled_exception()
    {
        var json = ValidDocument().Build();
        json = json[..^1] + ",\"fooZZ\":1}";

        var result = ValidateWithInvalidUtf8(json);

        result.IsValid.ShouldBeFalse();
        result.Failures.Single().ShouldStartWith("The file is not valid JSON:");
    }

    [Fact]
    public void A_cancelled_token_stops_validation_before_it_reads_anything()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ValidDocument().Build()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(() => _sut.Execute(stream, cancelled.Token));
        stream.Position.ShouldBe(0);
    }

    // A client that disconnects mid-validation must stop the work at the next buffer refill, not run a
    // dead request to the end of a 250 MB file.
    [Fact]
    public void Cancelling_during_validation_stops_it_at_the_next_buffer_refill()
    {
        var rooms = Enumerable.Range(0, 3000).Select(i => (object?)new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString(), ["name"] = $"Room {i}", ["createdAtUtc"] = "2026-01-05T08:15:00+00:00", ["archivedAt"] = null,
        }).ToList();
        var bytes = Encoding.UTF8.GetBytes(ValidDocument().With("rooms", rooms).Build());
        bytes.Length.ShouldBeGreaterThan(3 * 64 * 1024);
        using var cancel = new CancellationTokenSource();
        using var stream = new CancelAfterReadsStream(new MemoryStream(bytes), cancel, reads: 3);

        Should.Throw<OperationCanceledException>(() => _sut.Execute(stream, cancel.Token));

        stream.Reads.ShouldBeLessThan(10);
    }

    private sealed class CancelAfterReadsStream(Stream inner, CancellationTokenSource source, int reads) : Stream
    {
        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (++Reads == reads)
            {
                source.Cancel();
            }

            return inner.Read(buffer);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public int LargestReadRequest { get; private set; }

        public long TotalBytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count), count);

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer), buffer.Length);

        private int Count(int read, int requested)
        {
            LargestReadRequest = Math.Max(LargestReadRequest, requested);
            TotalBytesRead += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // prefix + fill bytes + suffix generated on read, so a "250 MB single string" costs the test nothing.
    private sealed class PatternStream(string prefix, byte fill, long fillLength, string suffix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private readonly byte[] _suffix = Encoding.UTF8.GetBytes(suffix);

        public override long Length => _prefix.Length + fillLength + _suffix.Length;
        public override long Position { get; set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var written = 0;
            while (written < buffer.Length && Position < Length)
            {
                if (Position < _prefix.Length)
                {
                    buffer[written] = _prefix[Position];
                }
                else if (Position < _prefix.Length + fillLength)
                {
                    var run = (int)Math.Min(buffer.Length - written, _prefix.Length + fillLength - Position);
                    buffer.Slice(written, run).Fill(fill);
                    written += run;
                    Position += run;
                    continue;
                }
                else
                {
                    buffer[written] = _suffix[Position - _prefix.Length - fillLength];
                }

                written++;
                Position++;
            }

            return written;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin == SeekOrigin.Begin ? offset : throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
