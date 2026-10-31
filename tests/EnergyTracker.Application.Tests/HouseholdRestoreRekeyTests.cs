using Shouldly;

namespace EnergyTracker.Application.Tests;

// Story 11.14 (audit C10): a restore assigns every entity a new id and every reference follows its
// target, so a file can never collide with a row of any Household.
public class HouseholdRestoreRekeyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // A counter-based id source: ids it hands out can never equal the Guid.NewGuid() ids of the file.
    private static Func<Guid> Counter()
    {
        var n = 0;
        return () => new Guid(++n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed record Fixture(
        MainMeterExportDto MainMeter,
        RoomExportDto Room,
        PowerPointExportDto PowerPoint,
        DeviceExportDto Device,
        MeterReadingExportDto Reading,
        MeterReadingExportDto PreviousReading,
        MeterRegressionPromptExportDto Prompt,
        TariffExportDto Tariff,
        EventExportDto RoomEvent,
        EventExportDto PowerPointEvent,
        EventExportDto DeviceEvent,
        EventExportDto UntaggedEvent,
        SmartPlugReadingExportDto PlugReading,
        SmartPlugReadingExportDto UnmappedPlugReading,
        StatusSnapshotExportDto Snapshot,
        AuditCorrectionExportDto ReadingCorrection,
        AuditCorrectionExportDto TariffCorrection,
        AuditCorrectionExportDto DeviceCorrection,
        AuditCorrectionExportDto OrphanCorrection,
        AuditCorrectionExportDto UnknownTypeCorrection,
        AuditCorrectionExportDto PlugCorrection,
        AuditCorrectionExportDto OrphanPlugCorrection)
    {
        public HouseholdExportResult ToFile() => new(
            "v2", T0,
            new HouseholdSettingsExportDto(Guid.NewGuid(), T0, "de-DE", "EUR", 3500m, 100m, 45, 3, true, true, "label"),
            [],
            MainMeter,
            [Reading, PreviousReading],
            [Prompt],
            [Tariff],
            [RoomEvent, PowerPointEvent, DeviceEvent, UntaggedEvent],
            [Room],
            [PowerPoint],
            [Device],
            [PlugReading, UnmappedPlugReading],
            [Snapshot],
            [ReadingCorrection, TariffCorrection, DeviceCorrection, OrphanCorrection, UnknownTypeCorrection, PlugCorrection, OrphanPlugCorrection]);
    }

    private static Fixture NewFixture()
    {
        var mainMeter = new MainMeterExportDto(Guid.NewGuid(), T0, 999.9m);
        var room = new RoomExportDto(Guid.NewGuid(), "Kitchen", T0, null);
        var powerPoint = new PowerPointExportDto(Guid.NewGuid(), room.Id, "Outlet", T0, null);
        var device = new DeviceExportDto(Guid.NewGuid(), powerPoint.Id, "Kettle", T0, null);
        var reading = new MeterReadingExportDto(Guid.NewGuid(), mainMeter.Id, 120m, T0.AddDays(1), Guid.NewGuid(), T0);
        var previous = new MeterReadingExportDto(Guid.NewGuid(), mainMeter.Id, 100m, T0, Guid.NewGuid(), T0);
        var prompt = new MeterRegressionPromptExportDto(Guid.NewGuid(), mainMeter.Id, reading.Id, previous.Id, T0, null, null, 999.9m);
        var tariff = new TariffExportDto(Guid.NewGuid(), 8.5m, 0.32m, "EUR", T0, 12, T0);
        EventExportDto Event(string? type, Guid? id) =>
            new(Guid.NewGuid(), "cooked", T0, T0, type, id, type is null ? null : "name", null, null);
        AuditCorrectionExportDto Audit(string type, Guid id) =>
            new(Guid.NewGuid(), type, id, "Field", "1", "2", T0);

        var plugReading = new SmartPlugReadingExportDto(Guid.NewGuid(), powerPoint.Id, "Kitchen", "Outlet", "Kettle", T0, T0.AddMinutes(15), 0.5m);
        return new Fixture(
            mainMeter, room, powerPoint, device, reading, previous, prompt, tariff,
            Event("Room", room.Id), Event("PowerPoint", powerPoint.Id), Event("Device", device.Id), Event(null, null),
            plugReading,
            new SmartPlugReadingExportDto(Guid.NewGuid(), null, "Kitchen", "Gone", "Kettle", T0, T0.AddMinutes(15), 0.5m),
            new StatusSnapshotExportDto(Guid.NewGuid(), "withinrange", 10m, 12m, false, T0),
            Audit("MeterReading", reading.Id), Audit("Tariff", tariff.Id), Audit("Device", device.Id),
            Audit("MeterReading", Guid.NewGuid()),
            Audit("Gadget", Guid.NewGuid()),
            Audit("SmartPlugReading", plugReading.Id),
            Audit("SmartPlugReading", Guid.NewGuid()));
    }

    private static HouseholdRestoreRekey Sut(Fixture f, Func<Guid>? idSource = null) => new(f.ToFile(), idSource);

    [Fact]
    public void Every_category_gets_new_ids_that_are_unique_within_the_category()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());

        var ids = new List<(string Category, Guid Old, Guid New)>
        {
            ("MainMeter", f.MainMeter.Id, sut.Rekey(f.MainMeter).Id),
            ("Room", f.Room.Id, sut.Rekey(f.Room).Id),
            ("PowerPoint", f.PowerPoint.Id, sut.Rekey(f.PowerPoint).Id),
            ("Device", f.Device.Id, sut.Rekey(f.Device).Id),
            ("MeterReading", f.Reading.Id, sut.Rekey(f.Reading).Id),
            ("MeterReading", f.PreviousReading.Id, sut.Rekey(f.PreviousReading).Id),
            ("Prompt", f.Prompt.Id, sut.Rekey(f.Prompt).Id),
            ("Tariff", f.Tariff.Id, sut.Rekey(f.Tariff).Id),
            ("Event", f.RoomEvent.Id, sut.Rekey(f.RoomEvent).Id),
            ("Event", f.PowerPointEvent.Id, sut.Rekey(f.PowerPointEvent).Id),
            ("Event", f.DeviceEvent.Id, sut.Rekey(f.DeviceEvent).Id),
            ("Event", f.UntaggedEvent.Id, sut.Rekey(f.UntaggedEvent).Id),
            ("SmartPlugReading", f.PlugReading.Id, sut.Rekey(f.PlugReading).Id),
            ("SmartPlugReading", f.UnmappedPlugReading.Id, sut.Rekey(f.UnmappedPlugReading).Id),
            ("StatusSnapshot", f.Snapshot.Id, sut.Rekey(f.Snapshot).Id),
            ("AuditCorrection", f.ReadingCorrection.Id, sut.Rekey(f.ReadingCorrection).Id),
            ("AuditCorrection", f.TariffCorrection.Id, sut.Rekey(f.TariffCorrection).Id),
        };

        ids.ShouldAllBe(x => x.Old != x.New);
        ids.GroupBy(x => x.Category).ShouldAllBe(g => g.Select(x => x.New).Distinct().Count() == g.Count());
    }

    [Fact]
    public void Idempotency_keys_are_regenerated_and_distinct_per_reading()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());

        var a = sut.Rekey(f.Reading);
        var b = sut.Rekey(f.PreviousReading);

        a.IdempotencyKey.ShouldNotBe(f.Reading.IdempotencyKey);
        b.IdempotencyKey.ShouldNotBe(f.PreviousReading.IdempotencyKey);
        a.IdempotencyKey.ShouldNotBe(b.IdempotencyKey);
        a.IdempotencyKey.ShouldNotBe(a.Id);
    }

    [Fact]
    public void Foreign_key_references_follow_their_targets()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());

        var room = sut.Rekey(f.Room);
        var powerPoint = sut.Rekey(f.PowerPoint);
        var device = sut.Rekey(f.Device);
        var mainMeter = sut.Rekey(f.MainMeter);
        var reading = sut.Rekey(f.Reading);
        var previous = sut.Rekey(f.PreviousReading);
        var prompt = sut.Rekey(f.Prompt);
        var plug = sut.Rekey(f.PlugReading);

        powerPoint.RoomId.ShouldBe(room.Id);
        device.PowerPointId.ShouldBe(powerPoint.Id);
        reading.MainMeterId.ShouldBe(mainMeter.Id);
        previous.MainMeterId.ShouldBe(mainMeter.Id);
        prompt.MainMeterId.ShouldBe(mainMeter.Id);
        prompt.MeterReadingId.ShouldBe(reading.Id);
        prompt.PreviousMeterReadingId.ShouldBe(previous.Id);
        plug.PowerPointId.ShouldBe(powerPoint.Id);
    }

    [Fact]
    public void A_smart_plug_reading_without_a_power_point_stays_without_one()
    {
        var f = NewFixture();

        var plug = Sut(f, Counter()).Rekey(f.UnmappedPlugReading);

        plug.PowerPointId.ShouldBeNull();
        plug.Id.ShouldNotBe(f.UnmappedPlugReading.Id);
    }

    [Fact]
    public void Event_tags_follow_Room_PowerPoint_and_Device_and_an_untagged_event_stays_untagged()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());
        var room = sut.Rekey(f.Room);
        var powerPoint = sut.Rekey(f.PowerPoint);
        var device = sut.Rekey(f.Device);

        sut.Rekey(f.RoomEvent).TaggedEntityId.ShouldBe(room.Id);
        sut.Rekey(f.PowerPointEvent).TaggedEntityId.ShouldBe(powerPoint.Id);
        sut.Rekey(f.DeviceEvent).TaggedEntityId.ShouldBe(device.Id);
        var untagged = sut.Rekey(f.UntaggedEvent);
        untagged.TaggedEntityId.ShouldBeNull();
        untagged.TaggedEntityType.ShouldBeNull();
    }

    [Fact]
    public void Event_tag_type_matching_is_case_insensitive_and_the_type_text_is_kept()
    {
        var f = NewFixture();
        var tagged = f.RoomEvent with { TaggedEntityType = "room" };
        var file = f.ToFile() with { Events = [tagged] };
        var sut = new HouseholdRestoreRekey(file, Counter());
        var room = sut.Rekey(f.Room);

        var result = sut.Rekey(tagged);

        result.TaggedEntityId.ShouldBe(room.Id);
        result.TaggedEntityType.ShouldBe("room");
    }

    [Fact]
    public void Audit_corrections_follow_MeterReading_Tariff_and_Device()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());
        var reading = sut.Rekey(f.Reading);
        var tariff = sut.Rekey(f.Tariff);
        var device = sut.Rekey(f.Device);

        sut.Rekey(f.ReadingCorrection).EntityId.ShouldBe(reading.Id);
        sut.Rekey(f.TariffCorrection).EntityId.ShouldBe(tariff.Id);
        sut.Rekey(f.DeviceCorrection).EntityId.ShouldBe(device.Id);
    }

    // Found by Story 11.14 Part R: the smart plug import's boundary correction writes EntityType
    // "SmartPlugReading", and a restore left its EntityId pointing at nothing.
    [Fact]
    public void An_audit_correction_follows_the_smart_plug_reading_it_points_at_and_only_that_one_is_mapped()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());

        var reading = sut.Rekey(f.PlugReading);
        var correction = sut.Rekey(f.PlugCorrection);

        correction.EntityId.ShouldBe(reading.Id);
        correction.EntityId.ShouldNotBe(f.PlugCorrection.EntityId);
        // A reading nobody points at is still re-keyed, and the audit lookalike with no target stays as it was.
        sut.Rekey(f.UnmappedPlugReading).Id.ShouldNotBe(f.UnmappedPlugReading.Id);
        sut.Rekey(f.OrphanPlugCorrection).EntityId.ShouldBe(f.OrphanPlugCorrection.EntityId);
    }

    [Fact]
    public void An_audit_correction_for_an_id_the_file_does_not_contain_or_an_unknown_type_is_kept_as_it_is()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());

        var orphan = sut.Rekey(f.OrphanCorrection);
        var unknown = sut.Rekey(f.UnknownTypeCorrection);

        orphan.EntityId.ShouldBe(f.OrphanCorrection.EntityId);
        orphan.EntityType.ShouldBe("MeterReading");
        unknown.EntityId.ShouldBe(f.UnknownTypeCorrection.EntityId);
        unknown.EntityType.ShouldBe("Gadget");
        // The correction row itself still gets a new id.
        orphan.Id.ShouldNotBe(f.OrphanCorrection.Id);
        unknown.Id.ShouldNotBe(f.UnknownTypeCorrection.Id);
    }

    [Fact]
    public void An_event_tag_pointing_at_an_id_the_file_does_not_contain_is_kept_as_it_is()
    {
        var f = NewFixture();
        var stray = f.RoomEvent with { TaggedEntityId = Guid.NewGuid() };
        var sut = new HouseholdRestoreRekey(f.ToFile() with { Events = [stray] }, Counter());

        sut.Rekey(stray).TaggedEntityId.ShouldBe(stray.TaggedEntityId);
    }

    [Fact]
    public void Everything_except_ids_and_references_is_copied_unchanged()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());

        sut.Rekey(f.Room).ShouldBe(f.Room with { Id = sut.Rekey(f.Room).Id });
        var reading = sut.Rekey(f.Reading);
        reading.KwhValue.ShouldBe(f.Reading.KwhValue);
        reading.ReadingTimestamp.ShouldBe(f.Reading.ReadingTimestamp);
        reading.CreatedAtUtc.ShouldBe(f.Reading.CreatedAtUtc);
        var plug = sut.Rekey(f.PlugReading);
        plug.RoomName.ShouldBe("Kitchen");
        plug.KwhValue.ShouldBe(0.5m);
        var evt = sut.Rekey(f.RoomEvent);
        evt.Description.ShouldBe(f.RoomEvent.Description);
        evt.TaggedEntityName.ShouldBe(f.RoomEvent.TaggedEntityName);
        sut.Rekey(f.Prompt).DigitCapacityKwh.ShouldBe(999.9m);
    }

    [Fact]
    public void A_file_with_no_main_meter_and_no_rows_rekeys_without_error()
    {
        var f = NewFixture();
        var empty = f.ToFile() with
        {
            MainMeter = null,
            MeterReadings = [],
            MeterRegressionPrompts = [],
            Tariffs = [],
            Events = [],
            Rooms = [],
            PowerPoints = [],
            Devices = [],
            SmartPlugReadings = [],
            StatusSnapshots = [],
            AuditCorrections = [],
        };

        Should.NotThrow(() => new HouseholdRestoreRekey(empty, Counter()));
    }

    [Fact]
    public void Two_runs_over_the_same_input_assign_different_ids_by_default()
    {
        var f = NewFixture();
        var file = f.ToFile();

        var first = new HouseholdRestoreRekey(file).Rekey(f.Room).Id;
        var second = new HouseholdRestoreRekey(file).Rekey(f.Room).Id;

        first.ShouldNotBe(f.Room.Id);
        second.ShouldNotBe(f.Room.Id);
        first.ShouldNotBe(second);
    }

    [Fact]
    public void The_input_records_are_not_mutated()
    {
        var f = NewFixture();
        var file = f.ToFile();
        var roomBefore = file.Rooms.Single();
        var powerPointBefore = file.PowerPoints.Single();
        var sut = new HouseholdRestoreRekey(file, Counter());

        _ = sut.Rekey(f.Room);
        _ = sut.Rekey(f.PowerPoint);

        file.Rooms.Single().ShouldBe(roomBefore);
        file.PowerPoints.Single().ShouldBe(powerPointBefore);
        file.PowerPoints.Single().RoomId.ShouldBe(f.Room.Id);
    }

    [Fact]
    public void The_same_source_row_maps_to_the_same_new_id_every_time_it_is_asked()
    {
        var f = NewFixture();
        var sut = Sut(f, Counter());

        // A reference must keep following its target no matter in which order the categories are mapped.
        var powerPointFirst = sut.Rekey(f.PowerPoint);
        var room = sut.Rekey(f.Room);

        powerPointFirst.RoomId.ShouldBe(room.Id);
    }
}
