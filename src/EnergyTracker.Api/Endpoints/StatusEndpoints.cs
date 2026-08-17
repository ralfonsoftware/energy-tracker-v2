using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class StatusEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Same shape as MeterReadingEndpoints.TryGetHouseholdId — copied rather than referenced
    // across files (that helper is private to its own class); Status is Household-scoped exactly
    // like MeterReading.
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

    public static RouteGroupBuilder MapStatusEndpoints(this RouteGroupBuilder api)
    {
        // Singleton resource, not a collection — /api/status, not plural (consistent with
        // /api/session, /health). Per Consistency Conventions: only the current Status value and
        // its supporting figures — drill-down data (Trend History, FR-8) is always a separate
        // endpoint, never merged in here.
        api.MapGet("/status", async (
            ICurrentHouseholdAccessor householdAccessor,
            GetCurrentStatus getCurrentStatus,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            var result = await getCurrentStatus.ExecuteAsync(householdId, cancellationToken);
            // 200 with a null body when undefined (AC #6) — same "is there one?" shape as
            // GET /api/meter-regression-prompts/open, not a 404/204. Story 2.5's onboarding empty
            // state is driven by this null, not by an error response.
            return Results.Ok(result is null ? null : ToResponse(result));
        });

        return api;
    }

    private static StatusResponse ToResponse(CurrentStatusResult result) => new(
        ToStatusString(result.Status),
        result.PaceToDateKwh,
        result.BaselineToDateKwh,
        result.IsLowConfidence);

    private static string ToStatusString(Status status) => status switch
    {
        Status.WithinRange => "withinRange",
        Status.BelowBaseline => "belowBaseline",
        Status.Trending => "trending",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

public record StatusResponse(string Status, decimal PaceToDateKwh, decimal BaselineToDateKwh, bool IsLowConfidence);
