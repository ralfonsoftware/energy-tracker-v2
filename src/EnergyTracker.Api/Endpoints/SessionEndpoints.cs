using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Api.Endpoints;

public static class SessionEndpoints
{
    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder api)
    {
        // Singleton resource, not a collection — /api/session, not plural (consistent with /health).
        // Sits behind the /api group's auth requirement, so an unauthenticated call 401s; the SPA's
        // response to that 401 is what triggers navigation to /login (AC #1).
        api.MapGet("/session", async (
            ICurrentHouseholdAccessor householdAccessor,
            EnergyTrackerDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.HouseholdId;
            if (householdId is null)
            {
                return Results.Ok(new SessionResponse(HasHousehold: false, HouseholdId: null, Locale: null, Currency: null));
            }

            var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, cancellationToken);
            return Results.Ok(new SessionResponse(HasHousehold: true, household.Id, household.Locale, household.Currency));
        });

        return api;
    }
}

public record SessionResponse(bool HasHousehold, Guid? HouseholdId, string? Locale, string? Currency);
