namespace EnergyTracker.Application;

/// <summary>Assigns every restored entity a new id and regenerates every meter-reading idempotency key, and makes each reference in the file follow its target, so a restore can never collide with a row of any Household (audit C10, AC #1, #3). Pure: no I/O, no clock, no database; a reference that resolves to nothing in the file is kept as it is.</summary>
public sealed class HouseholdRestoreRekey
{
    private readonly Func<Guid> _newId;
    private readonly Dictionary<Guid, Guid> _mainMeters = [];
    private readonly Dictionary<Guid, Guid> _rooms = [];
    private readonly Dictionary<Guid, Guid> _powerPoints = [];
    private readonly Dictionary<Guid, Guid> _devices = [];
    private readonly Dictionary<Guid, Guid> _meterReadings = [];
    private readonly Dictionary<Guid, Guid> _tariffs = [];

    // The old-to-new maps are built once, from the ids the file already holds, and grow by item count
    // only: the DTO lists are mapped one category at a time by the caller, never copied here (the
    // SmartPlugReading list can hold hundreds of thousands of rows and needs no map at all).
    public HouseholdRestoreRekey(HouseholdExportResult source, Func<Guid>? newId = null)
    {
        _newId = newId ?? Guid.NewGuid;
        if (source.MainMeter is { } mainMeter)
        {
            Assign(_mainMeters, mainMeter.Id);
        }

        foreach (var room in source.Rooms)
        {
            Assign(_rooms, room.Id);
        }

        foreach (var powerPoint in source.PowerPoints)
        {
            Assign(_powerPoints, powerPoint.Id);
        }

        foreach (var device in source.Devices)
        {
            Assign(_devices, device.Id);
        }

        foreach (var reading in source.MeterReadings)
        {
            Assign(_meterReadings, reading.Id);
        }

        foreach (var tariff in source.Tariffs)
        {
            Assign(_tariffs, tariff.Id);
        }
    }

    public MainMeterExportDto Rekey(MainMeterExportDto dto) => dto with { Id = Assign(_mainMeters, dto.Id) };

    public RoomExportDto Rekey(RoomExportDto dto) => dto with { Id = Assign(_rooms, dto.Id) };

    public PowerPointExportDto Rekey(PowerPointExportDto dto) =>
        dto with { Id = Assign(_powerPoints, dto.Id), RoomId = Follow(_rooms, dto.RoomId) };

    public DeviceExportDto Rekey(DeviceExportDto dto) =>
        dto with { Id = Assign(_devices, dto.Id), PowerPointId = Follow(_powerPoints, dto.PowerPointId) };

    // The idempotency key has a global unique index, so keeping it would keep a collision source (Q6).
    public MeterReadingExportDto Rekey(MeterReadingExportDto dto) => dto with
    {
        Id = Assign(_meterReadings, dto.Id),
        MainMeterId = Follow(_mainMeters, dto.MainMeterId),
        IdempotencyKey = _newId(),
    };

    public MeterRegressionPromptExportDto Rekey(MeterRegressionPromptExportDto dto) => dto with
    {
        Id = _newId(),
        MainMeterId = Follow(_mainMeters, dto.MainMeterId),
        MeterReadingId = Follow(_meterReadings, dto.MeterReadingId),
        PreviousMeterReadingId = Follow(_meterReadings, dto.PreviousMeterReadingId),
    };

    public TariffExportDto Rekey(TariffExportDto dto) => dto with { Id = Assign(_tariffs, dto.Id) };

    // TaggedEntityId has no foreign key: no database error would reveal a stale one, so it is resolved
    // by TaggedEntityType here (case-insensitive; the type text itself is kept).
    public EventExportDto Rekey(EventExportDto dto) => dto with
    {
        Id = _newId(),
        TaggedEntityId = dto.TaggedEntityId is { } taggedId ? FollowTag(dto.TaggedEntityType, taggedId) : null,
    };

    public SmartPlugReadingExportDto Rekey(SmartPlugReadingExportDto dto) => dto with
    {
        Id = _newId(),
        PowerPointId = dto.PowerPointId is { } powerPointId ? Follow(_powerPoints, powerPointId) : null,
    };

    public StatusSnapshotExportDto Rekey(StatusSnapshotExportDto dto) => dto with { Id = _newId() };

    // EntityId has no foreign key either; EntityType "MeterReading" and "Tariff" are the two written
    // today, "Room", "PowerPoint" and "Device" are mapped too if a file carries them.
    public AuditCorrectionExportDto Rekey(AuditCorrectionExportDto dto) =>
        dto with { Id = _newId(), EntityId = FollowAuditTarget(dto.EntityType, dto.EntityId) };

    // The new id of an entity the file contains. An id the constructor did not see (an entity passed in
    // that is not part of the source) is given one on first use and keeps it, so a later reference to
    // it still resolves.
    private Guid Assign(Dictionary<Guid, Guid> map, Guid oldId)
    {
        if (!map.TryGetValue(oldId, out var newId))
        {
            newId = _newId();
            map[oldId] = newId;
        }

        return newId;
    }

    // A reference follows its target; one that resolves to nothing is kept (the writer's own foreign keys
    // stay the backstop for a file the validator did not check).
    private static Guid Follow(Dictionary<Guid, Guid> map, Guid oldId) => map.TryGetValue(oldId, out var newId) ? newId : oldId;

    private Guid FollowTag(string? taggedEntityType, Guid oldId)
    {
        var map = taggedEntityType?.ToLowerInvariant() switch
        {
            "room" => _rooms,
            "powerpoint" => _powerPoints,
            "device" => _devices,
            _ => null,
        };
        return map is null ? oldId : Follow(map, oldId);
    }

    private Guid FollowAuditTarget(string entityType, Guid oldId)
    {
        var map = entityType.ToLowerInvariant() switch
        {
            "meterreading" => _meterReadings,
            "tariff" => _tariffs,
            "room" => _rooms,
            "powerpoint" => _powerPoints,
            "device" => _devices,
            _ => null,
        };
        return map is null ? oldId : Follow(map, oldId);
    }
}
