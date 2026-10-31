using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using static EnergyTracker.Api.Endpoints.TaggingScaffoldMapping;

namespace EnergyTracker.Api.Endpoints;

internal static class RoomEndpoints
{
    internal static void MapRoomEndpoints(RouteGroupBuilder api)
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
}
