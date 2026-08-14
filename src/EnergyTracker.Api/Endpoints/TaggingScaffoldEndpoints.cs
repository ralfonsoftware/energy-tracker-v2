using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class TaggingScaffoldEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Shared by all 12 route handlers below — every one of them needs the caller's HouseholdId
    // before doing anything else, and 403s the same way when the principal doesn't have one.
    private static bool TryGetHouseholdId(ICurrentHouseholdAccessor householdAccessor, out Guid householdId, out IResult? forbidden)
    {
        if (householdAccessor.HouseholdId is { } id)
        {
            householdId = id;
            forbidden = null;
            return true;
        }

        householdId = default;
        forbidden = Results.Problem(detail: NoHouseholdDetail, statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    public static RouteGroupBuilder MapTaggingScaffoldEndpoints(this RouteGroupBuilder api)
    {
        MapRoomEndpoints(api);
        MapPowerPointEndpoints(api);
        MapDeviceEndpoints(api);

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
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

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
            ICurrentHouseholdAccessor householdAccessor,
            ITaggingScaffoldRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

            var rooms = await repository.ListRoomsAsync(cancellationToken);
            return Results.Ok(rooms.Select(ToResponse));
        });

        api.MapPut("/rooms/{id}", async (
            Guid id,
            RenameRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            RenameRoom renameRoom,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

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
            ICurrentHouseholdAccessor householdAccessor,
            ArchiveRoom archiveRoom,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

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
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

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
            ICurrentHouseholdAccessor householdAccessor,
            ITaggingScaffoldRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

            var powerPoints = await repository.ListPowerPointsAsync(cancellationToken);
            return Results.Ok(powerPoints.Select(ToResponse));
        });

        api.MapPut("/power-points/{id}", async (
            Guid id,
            RenameRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            RenamePowerPoint renamePowerPoint,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

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
            ICurrentHouseholdAccessor householdAccessor,
            ArchivePowerPoint archivePowerPoint,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

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
    }

    private static void MapDeviceEndpoints(RouteGroupBuilder api)
    {
        api.MapPost("/devices", async (
            CreateDeviceRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreateDevice createDevice,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

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
            ICurrentHouseholdAccessor householdAccessor,
            ITaggingScaffoldRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

            var devices = await repository.ListDevicesAsync(cancellationToken);
            return Results.Ok(devices.Select(ToResponse));
        });

        api.MapPut("/devices/{id}", async (
            Guid id,
            RenameRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            RenameDevice renameDevice,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

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
            ICurrentHouseholdAccessor householdAccessor,
            ArchiveDevice archiveDevice,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

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
    }

    private static RoomResponse ToResponse(Room room) => new(room.Id, room.Name, room.ArchivedAt);

    private static PowerPointResponse ToResponse(PowerPoint powerPoint) => new(powerPoint.Id, powerPoint.RoomId, powerPoint.Name, powerPoint.ArchivedAt);

    private static DeviceResponse ToResponse(Device device) => new(device.Id, device.PowerPointId, device.Name, device.ArchivedAt);
}

public record CreateRoomRequest(string Name);

public record CreatePowerPointRequest(Guid RoomId, string Name);

public record CreateDeviceRequest(Guid PowerPointId, string Name);

public record RenameRequest(string Name);

public record RoomResponse(Guid Id, string Name, DateTimeOffset? ArchivedAt);

public record PowerPointResponse(Guid Id, Guid RoomId, string Name, DateTimeOffset? ArchivedAt);

public record DeviceResponse(Guid Id, Guid PowerPointId, string Name, DateTimeOffset? ArchivedAt);
