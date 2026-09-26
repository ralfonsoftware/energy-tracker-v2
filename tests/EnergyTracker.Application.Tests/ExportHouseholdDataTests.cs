using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class ExportHouseholdDataTests
{
    private readonly IHouseholdExportReader _reader = Substitute.For<IHouseholdExportReader>();

    private ExportHouseholdData Sut(AiPlausibilityBackendOptions? backendOptions = null) =>
        new(_reader, backendOptions ?? new AiPlausibilityBackendOptions(Configured: false, Label: null, Model: "default"));

    private static Household NewHousehold(Guid householdId) => new()
    {
        Id = householdId,
        Locale = "de-DE",
        Currency = "EUR",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        YearlyBaselineKwh = 3500m,
        TrendingThresholdKwh = 100m,
        LowConfidenceGapDays = 45,
        TariffCheckCadenceMonths = 3,
        AiPlausibilityEnabled = true,
    };

    private static HouseholdExportData EmptyExportData(Household household) => new(
        household,
        HouseholdMembers: ToAsyncEnumerable<HouseholdMember>([]),
        MainMeter: null,
        MeterReadings: ToAsyncEnumerable<MeterReading>([]),
        MeterRegressionPrompts: ToAsyncEnumerable<MeterRegressionPrompt>([]),
        Tariffs: ToAsyncEnumerable<Tariff>([]),
        Events: ToAsyncEnumerable<Event>([]),
        Rooms: ToAsyncEnumerable<Room>([]),
        PowerPoints: ToAsyncEnumerable<PowerPoint>([]),
        Devices: ToAsyncEnumerable<Device>([]),
        SmartPlugReadings: ToAsyncEnumerable<SmartPlugReading>([]),
        StatusSnapshots: ToAsyncEnumerable<StatusSnapshot>([]),
        AuditCorrections: ToAsyncEnumerable<AuditCorrection>([]));

    // HouseholdExportData's collections are IAsyncEnumerable<T> (spec-household-export-oom-fix.md)
    // — these two helpers bridge test fixtures (plain in-memory lists) to/from that shape without
    // adding a System.Linq.Async package dependency for a couple of 4-line iterators.
    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> source)
    {
        await Task.Yield();
        foreach (var item in source)
        {
            yield return item;
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    [Fact]
    public async Task Sets_the_v2_formatVersion_and_a_fresh_exportedAtUtc_timestamp()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(EmptyExportData(household));
        var before = DateTimeOffset.UtcNow;

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.FormatVersion.ShouldBe("v2");
        result.ExportedAtUtc.ShouldBeGreaterThanOrEqualTo(before);
        result.ExportedAtUtc.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Queries_the_reader_for_exactly_the_requested_Household_and_no_other()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(EmptyExportData(household));

        await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        await _reader.Received(1).GetExportDataAsync(householdId, Arg.Any<CancellationToken>());
        await _reader.DidNotReceive().GetExportDataAsync(Arg.Is<Guid>(id => id != householdId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Copies_Household_settings_fields_and_never_the_YearlyBaseline_preset_a_household_never_set()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(EmptyExportData(household));

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.Household.Id.ShouldBe(householdId);
        result.Household.Locale.ShouldBe("de-DE");
        result.Household.Currency.ShouldBe("EUR");
        result.Household.YearlyBaselineKwh.ShouldBe(3500m);
        result.Household.TrendingThresholdKwh.ShouldBe(100m);
        result.Household.LowConfidenceGapDays.ShouldBe(45);
        result.Household.TariffCheckCadenceMonths.ShouldBe(3);
        result.Household.AiPlausibilityEnabled.ShouldBeTrue();
    }

    // AD-19: only the non-secret half of the deployment-wide AI backend config is ever exported.
    [Fact]
    public async Task Includes_only_the_non_secret_AiPlausibilityBackendOptions_fields()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(EmptyExportData(household));
        var backendOptions = new AiPlausibilityBackendOptions(Configured: true, Label: "Local (LMStudio)", Model: "secret-model-id");

        var result = await Sut(backendOptions).ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.Household.AiPlausibilityBackendConfigured.ShouldBeTrue();
        result.Household.AiPlausibilityBackendLabel.ShouldBe("Local (LMStudio)");
        // Model never appears anywhere on the DTO — there is no property for it at all, which is
        // itself the guarantee; this assertion documents that intent for a future reader.
        typeof(HouseholdSettingsExportDto).GetProperty("Model").ShouldBeNull();
        typeof(HouseholdSettingsExportDto).GetProperty("AiPlausibilityBackendModel").ShouldBeNull();
    }

    [Fact]
    public async Task Every_decided_entity_category_is_present_in_the_result()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        var mainMeter = new MainMeter { Id = Guid.NewGuid(), HouseholdId = householdId, CreatedAtUtc = DateTimeOffset.UtcNow };
        var reading = new MeterReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeter.Id,
            KwhValue = 100m,
            ReadingTimestamp = DateTimeOffset.UtcNow,
            IdempotencyKey = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = mainMeter.Id,
            MeterReadingId = reading.Id,
            PreviousMeterReadingId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var tariff = new Tariff
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MonthlyBaseFee = 8.5m,
            PricePerKwh = 0.32m,
            Currency = "EUR",
            ContractStartDate = DateTimeOffset.UtcNow,
            ContractPeriodMonths = 12,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var @event = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "cooked 2h",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var room = new Room { Id = Guid.NewGuid(), HouseholdId = householdId, Name = "Kitchen", CreatedAtUtc = DateTimeOffset.UtcNow };
        var powerPoint = new PowerPoint
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, RoomId = room.Id, Name = "Outlet", CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var device = new Device
        {
            Id = Guid.NewGuid(), HouseholdId = householdId, PowerPointId = powerPoint.Id, Name = "Kettle", CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var smartPlugReading = new SmartPlugReading
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            PowerPointId = powerPoint.Id,
            RoomName = "Kitchen",
            PowerPointName = "Outlet",
            DeviceName = "Kettle",
            IntervalStart = DateTimeOffset.UtcNow.AddHours(-1),
            IntervalEnd = DateTimeOffset.UtcNow,
            KwhValue = 0.5m,
        };
        var snapshot = new StatusSnapshot
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Status = Status.WithinRange,
            PaceToDateKwh = 10m,
            BaselineToDateKwh = 12m,
            IsLowConfidence = false,
            ComputedAtUtc = DateTimeOffset.UtcNow,
        };
        var correction = new AuditCorrection
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            EntityType = "MeterReading",
            EntityId = reading.Id,
            FieldName = "KwhValue",
            OldValue = "90",
            NewValue = "100",
            CorrectedAtUtc = DateTimeOffset.UtcNow,
        };
        var member = new HouseholdMember
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            ExternalIssuer = "https://issuer.test/",
            ExternalSubjectId = "sub-1",
            DisplayName = "Ralf",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var data = new HouseholdExportData(
            household,
            ToAsyncEnumerable([member]),
            mainMeter,
            ToAsyncEnumerable([reading]),
            ToAsyncEnumerable([prompt]),
            ToAsyncEnumerable([tariff]),
            ToAsyncEnumerable([@event]),
            ToAsyncEnumerable([room]),
            ToAsyncEnumerable([powerPoint]),
            ToAsyncEnumerable([device]),
            ToAsyncEnumerable([smartPlugReading]),
            ToAsyncEnumerable([snapshot]),
            ToAsyncEnumerable([correction]));
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(data);

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        (await ToListAsync(result.HouseholdMembers)).Single().Id.ShouldBe(member.Id);
        result.MainMeter.ShouldNotBeNull();
        result.MainMeter!.Id.ShouldBe(mainMeter.Id);
        (await ToListAsync(result.MeterReadings)).Single().Id.ShouldBe(reading.Id);
        (await ToListAsync(result.MeterRegressionPrompts)).Single().Id.ShouldBe(prompt.Id);
        (await ToListAsync(result.Tariffs)).Single().Id.ShouldBe(tariff.Id);
        (await ToListAsync(result.Events)).Single().Id.ShouldBe(@event.Id);
        (await ToListAsync(result.Rooms)).Single().Id.ShouldBe(room.Id);
        (await ToListAsync(result.PowerPoints)).Single().Id.ShouldBe(powerPoint.Id);
        (await ToListAsync(result.Devices)).Single().Id.ShouldBe(device.Id);
        (await ToListAsync(result.SmartPlugReadings)).Single().Id.ShouldBe(smartPlugReading.Id);
        (await ToListAsync(result.StatusSnapshots)).Single().Id.ShouldBe(snapshot.Id);
        (await ToListAsync(result.AuditCorrections)).Single().Id.ShouldBe(correction.Id);
    }

    [Fact]
    public async Task MainMeter_is_null_when_the_Household_has_never_logged_a_reading()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(EmptyExportData(household));

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        result.MainMeter.ShouldBeNull();
    }

    // AD-10: TaggedEntityName/CorrelationDirection/CorrelationComputedAtUtc must be copied
    // unchanged — never recomputed against a live tag.
    [Fact]
    public async Task Copies_an_Events_opaque_snapshot_and_correlation_fields_unchanged()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        var computedAt = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var @event = new Event
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Description = "gaming session 3h",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            TaggedEntityType = "Room",
            TaggedEntityId = Guid.NewGuid(),
            TaggedEntityName = "Kitchen (archived name at write time)",
            CorrelationDirection = "Bump",
            CorrelationComputedAtUtc = computedAt,
        };
        var data = EmptyExportData(household) with { Events = ToAsyncEnumerable([@event]) };
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(data);

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        var exported = (await ToListAsync(result.Events)).Single();
        exported.TaggedEntityType.ShouldBe("Room");
        exported.TaggedEntityId.ShouldBe(@event.TaggedEntityId);
        exported.TaggedEntityName.ShouldBe("Kitchen (archived name at write time)");
        exported.CorrelationDirection.ShouldBe("Bump");
        exported.CorrelationComputedAtUtc.ShouldBe(computedAt);
    }

    // This codebase's manual per-field enum convention (no global JsonStringEnumConverter) —
    // MeterRegressionClassification/Status must serialize as lowercase strings.
    [Fact]
    public async Task Serializes_MeterRegressionPrompt_Classification_as_a_lowercase_string()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = Guid.NewGuid(),
            MeterReadingId = Guid.NewGuid(),
            PreviousMeterReadingId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Classification = MeterRegressionClassification.Rollover,
        };
        var data = EmptyExportData(household) with { MeterRegressionPrompts = ToAsyncEnumerable([prompt]) };
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(data);

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        (await ToListAsync(result.MeterRegressionPrompts)).Single().Classification.ShouldBe("rollover");
    }

    [Fact]
    public async Task An_unresolved_MeterRegressionPrompt_has_a_null_Classification()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        var prompt = new MeterRegressionPrompt
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            MainMeterId = Guid.NewGuid(),
            MeterReadingId = Guid.NewGuid(),
            PreviousMeterReadingId = Guid.NewGuid(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        var data = EmptyExportData(household) with { MeterRegressionPrompts = ToAsyncEnumerable([prompt]) };
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(data);

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        (await ToListAsync(result.MeterRegressionPrompts)).Single().Classification.ShouldBeNull();
    }

    [Fact]
    public async Task Serializes_StatusSnapshot_Status_as_a_lowercase_string()
    {
        var householdId = Guid.NewGuid();
        var household = NewHousehold(householdId);
        var snapshot = new StatusSnapshot
        {
            Id = Guid.NewGuid(),
            HouseholdId = householdId,
            Status = Status.Trending,
            PaceToDateKwh = 10m,
            BaselineToDateKwh = 8m,
            IsLowConfidence = false,
            ComputedAtUtc = DateTimeOffset.UtcNow,
        };
        var data = EmptyExportData(household) with { StatusSnapshots = ToAsyncEnumerable([snapshot]) };
        _reader.GetExportDataAsync(householdId, Arg.Any<CancellationToken>()).Returns(data);

        var result = await Sut().ExecuteAsync(householdId, TestContext.Current.CancellationToken);

        (await ToListAsync(result.StatusSnapshots)).Single().Status.ShouldBe("trending");
    }

    // SmartPlugImport is out of export scope — SmartPlugImportId must never appear on the DTO.
    [Fact]
    public void SmartPlugReadingExportDto_has_no_SmartPlugImportId_field()
    {
        typeof(SmartPlugReadingExportDto).GetProperty("SmartPlugImportId").ShouldBeNull();
    }
}
