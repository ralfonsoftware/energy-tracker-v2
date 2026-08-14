using EnergyTracker.Domain;

namespace EnergyTracker.Application.Ports;

/// <summary>
/// One port for the whole Room → Power Point → Device tagging scaffold — a single hierarchical
/// aggregate, not three parallel ports (matches IHouseholdRepository's one-port-per-aggregate
/// precedent). Find/List rely on AD-3's DbContext-level query filter to already scope results
/// to the current Household; only Add needs a HouseholdId to stamp onto a newly constructed entity.
/// </summary>
public interface ITaggingScaffoldRepository
{
    Task<Room?> FindRoomAsync(Guid roomId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Room>> ListRoomsAsync(CancellationToken cancellationToken);

    Task AddRoomAsync(Room room, CancellationToken cancellationToken);

    Task UpdateRoomAsync(Room room, CancellationToken cancellationToken);

    Task<PowerPoint?> FindPowerPointAsync(Guid powerPointId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PowerPoint>> ListPowerPointsAsync(CancellationToken cancellationToken);

    Task AddPowerPointAsync(PowerPoint powerPoint, CancellationToken cancellationToken);

    Task UpdatePowerPointAsync(PowerPoint powerPoint, CancellationToken cancellationToken);

    Task<Device?> FindDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Device>> ListDevicesAsync(CancellationToken cancellationToken);

    Task AddDeviceAsync(Device device, CancellationToken cancellationToken);

    Task UpdateDeviceAsync(Device device, CancellationToken cancellationToken);
}
