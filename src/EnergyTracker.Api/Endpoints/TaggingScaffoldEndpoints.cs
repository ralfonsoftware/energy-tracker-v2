using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class TaggingScaffoldEndpoints
{
    public static RouteGroupBuilder MapTaggingScaffoldEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        MapRoomEndpoints(household);
        MapPowerPointEndpoints(household);
        MapDeviceEndpoints(household);

        return api;
    }

    private static void MapRoomEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/rooms", async (
            CreateRoomRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreateRoom createRoom,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            try
            {
                var room = await createRoom.ExecuteAsync(householdId, request.Name, cancellationToken);
                return Results.Ok(ToResponse(room));
            }
            catch (TaggingScaffoldValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapGet("/rooms", async (
            ITaggingScaffoldRepository repository,
            CancellationToken cancellationToken) =>
        {
            var rooms = await repository.ListRoomsAsync(cancellationToken);
            return Results.Ok(rooms.Select(ToResponse));
        });

        api.MapPut("/rooms/{id}", async (
            Guid id,
            RenameRequest request,
            RenameRoom renameRoom,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var room = await renameRoom.ExecuteAsync(id, request.Name, cancellationToken);
                return Results.Ok(ToResponse(room));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (TaggingScaffoldValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapDelete("/rooms/{id}", async (
            Guid id,
            ArchiveRoom archiveRoom,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var room = await archiveRoom.ExecuteAsync(id, cancellationToken);
                return Results.Ok(ToResponse(room));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
        });
    }

    private static void MapPowerPointEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/power-points", async (
            CreatePowerPointRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreatePowerPoint createPowerPoint,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            try
            {
                var powerPoint = await createPowerPoint.ExecuteAsync(householdId, request.RoomId, request.Name, cancellationToken);
                return Results.Ok(ToResponse(powerPoint));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (TaggingScaffoldValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (TaggingScaffoldParentArchivedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/power-points", async (
            ITaggingScaffoldRepository repository,
            CancellationToken cancellationToken) =>
        {
            var powerPoints = await repository.ListPowerPointsAsync(cancellationToken);
            return Results.Ok(powerPoints.Select(ToResponse));
        });

        api.MapPut("/power-points/{id}", async (
            Guid id,
            RenameRequest request,
            RenamePowerPoint renamePowerPoint,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var powerPoint = await renamePowerPoint.ExecuteAsync(id, request.Name, cancellationToken);
                return Results.Ok(ToResponse(powerPoint));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (TaggingScaffoldValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapDelete("/power-points/{id}", async (
            Guid id,
            ArchivePowerPoint archivePowerPoint,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var powerPoint = await archivePowerPoint.ExecuteAsync(id, cancellationToken);
                return Results.Ok(ToResponse(powerPoint));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
        });

        api.MapPut("/power-points/{id}/room", async (
            Guid id,
            MovePowerPointRequest request,
            MovePowerPoint movePowerPoint,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var powerPoint = await movePowerPoint.ExecuteAsync(id, request.RoomId, cancellationToken);
                return Results.Ok(ToResponse(powerPoint));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (TaggingScaffoldValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (TaggingScaffoldParentArchivedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
    }

    private static void MapDeviceEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/devices", async (
            CreateDeviceRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreateDevice createDevice,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            try
            {
                var device = await createDevice.ExecuteAsync(householdId, request.PowerPointId, request.Name, cancellationToken);
                return Results.Ok(ToResponse(device));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (TaggingScaffoldValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (TaggingScaffoldParentArchivedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/devices", async (
            ITaggingScaffoldRepository repository,
            CancellationToken cancellationToken) =>
        {
            var devices = await repository.ListDevicesAsync(cancellationToken);
            return Results.Ok(devices.Select(ToResponse));
        });

        api.MapPut("/devices/{id}", async (
            Guid id,
            RenameRequest request,
            RenameDevice renameDevice,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var device = await renameDevice.ExecuteAsync(id, request.Name, cancellationToken);
                return Results.Ok(ToResponse(device));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (TaggingScaffoldValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        api.MapDelete("/devices/{id}", async (
            Guid id,
            ArchiveDevice archiveDevice,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var device = await archiveDevice.ExecuteAsync(id, cancellationToken);
                return Results.Ok(ToResponse(device));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
        });

        api.MapPut("/devices/{id}/power-point", async (
            Guid id,
            MoveDeviceRequest request,
            MoveDevice moveDevice,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var device = await moveDevice.ExecuteAsync(id, request.PowerPointId, cancellationToken);
                return Results.Ok(ToResponse(device));
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (TaggingScaffoldValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (TaggingScaffoldParentArchivedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
    }

    private static RoomResponse ToResponse(Room room) => new(room.Id, room.Name, room.ArchivedAt);

    private static PowerPointResponse ToResponse(PowerPoint powerPoint) => new(powerPoint.Id, powerPoint.RoomId, powerPoint.Name, powerPoint.ArchivedAt);

    private static DeviceResponse ToResponse(Device device) => new(device.Id, device.PowerPointId, device.Name, device.ArchivedAt);
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
