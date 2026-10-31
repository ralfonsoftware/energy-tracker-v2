using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

internal static class TaggingScaffoldMapping
{
    internal static RoomResponse ToResponse(Room room) => new(room.Id, room.Name, room.ArchivedAt);

    internal static PowerPointResponse ToResponse(PowerPoint powerPoint) => new(powerPoint.Id, powerPoint.RoomId, powerPoint.Name, powerPoint.ArchivedAt);

    internal static DeviceResponse ToResponse(Device device) => new(device.Id, device.PowerPointId, device.Name, device.ArchivedAt);
}

public record CreateRoomRequest(string Name);

public record CreatePowerPointRequest(Guid RoomId, string Name);

public record CreateDeviceRequest(Guid PowerPointId, string Name);

public record RenameRequest(string Name);

public record MovePowerPointRequest(Guid RoomId);

public record MoveDeviceRequest(Guid PowerPointId);

public record RoomResponse(Guid Id, string Name, DateTimeOffset? ArchivedAt);

public record PowerPointResponse(Guid Id, Guid RoomId, string Name, DateTimeOffset? ArchivedAt);

public record DeviceResponse(Guid Id, Guid PowerPointId, string Name, DateTimeOffset? ArchivedAt);
