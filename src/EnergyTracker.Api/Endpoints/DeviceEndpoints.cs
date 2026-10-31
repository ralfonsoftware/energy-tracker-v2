using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using static EnergyTracker.Api.Endpoints.TaggingScaffoldMapping;

namespace EnergyTracker.Api.Endpoints;

internal static class DeviceEndpoints
{
    internal static void MapDeviceEndpoints(RouteGroupBuilder api)
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
}
