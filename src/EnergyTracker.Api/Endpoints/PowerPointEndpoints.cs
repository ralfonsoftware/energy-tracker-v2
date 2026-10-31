using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using static EnergyTracker.Api.Endpoints.TaggingScaffoldMapping;

namespace EnergyTracker.Api.Endpoints;

internal static class PowerPointEndpoints
{
    internal static void MapPowerPointEndpoints(RouteGroupBuilder api)
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
}
