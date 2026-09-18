using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class EventEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Same shape as MeterReadingEndpoints.TryGetHouseholdId — copied rather than referenced across
    // files (that helper is private to its own class); an Event is Household-scoped exactly like
    // MeterReading.
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

    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/events", async (
            CreateEventRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreateEvent createEvent,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                var @event = await createEvent.ExecuteAsync(
                    householdId, request.Description, request.OccurredAt, request.TaggedEntityType, request.TaggedEntityId, cancellationToken);
                return Results.Ok(ToResponse(@event));
            }
            catch (EventValidationException ex)
            {
                return Problem(ex.Message, ex.ErrorCode, StatusCodes.Status400BadRequest);
            }
            catch (EventTaggedEntityNotFoundException ex)
            {
                return Problem(ex.Message, ex.ErrorCode, StatusCodes.Status404NotFound);
            }
            catch (EventTaggedEntityArchivedException ex)
            {
                return Problem(ex.Message, ex.ErrorCode, StatusCodes.Status409Conflict);
            }
        });

        return api;
    }

    // The detail string stays English for logs/API consumers; `errorCode` is the stable contract the
    // SPA maps to a localized catalog entry (AD-18) instead of echoing this text at the user.
    private static IResult Problem(string detail, string errorCode, int statusCode) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });

    private static EventResponse ToResponse(Event @event) =>
        new(@event.Id, @event.Description, @event.OccurredAt, @event.TaggedEntityType, @event.TaggedEntityName);
}

public record CreateEventRequest(string? Description, DateTimeOffset OccurredAt, string? TaggedEntityType, Guid? TaggedEntityId);

public record EventResponse(Guid Id, string Description, DateTimeOffset OccurredAt, string? TaggedEntityType, string? TaggedEntityName);
