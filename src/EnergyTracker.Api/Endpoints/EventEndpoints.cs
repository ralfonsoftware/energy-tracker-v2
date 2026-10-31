using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class EventEndpoints
{
    public static RouteGroupBuilder MapEventEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        household.MapPost("/events", async (
            CreateEventRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            CreateEvent createEvent,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

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

        household.MapGet("/events", async (
            GetEventHistory getEventHistory,
            CancellationToken cancellationToken,
            int page = 1,
            int pageSize = 20) =>
        {
            // No Household id here: AD-3's query filter does the scoping (see GetEventHistory/IEventRepository).
            try
            {
                var result = await getEventHistory.ExecuteAsync(page, pageSize, cancellationToken);
                return Results.Ok(ToHistoryPageResponse(result));
            }
            catch (EventValidationException ex)
            {
                return Problem(ex.Message, ex.ErrorCode, StatusCodes.Status400BadRequest);
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
        new(@event.Id, @event.Description, @event.OccurredAt, @event.TaggedEntityType, @event.TaggedEntityName, @event.CorrelationDirection);

    private static EventHistoryPageResponse ToHistoryPageResponse(EventHistoryPage page) =>
        new(
            Items: page.Items.Select(ToResponse).ToList(),
            TotalCount: page.TotalCount,
            Page: page.Page,
            PageSize: page.PageSize);
}

public record CreateEventRequest(string? Description, DateTimeOffset OccurredAt, string? TaggedEntityType, Guid? TaggedEntityId);

public record EventResponse(
    Guid Id, string Description, DateTimeOffset OccurredAt, string? TaggedEntityType, string? TaggedEntityName, string? CorrelationDirection);

public record EventHistoryPageResponse(IReadOnlyList<EventResponse> Items, int TotalCount, int Page, int PageSize);
