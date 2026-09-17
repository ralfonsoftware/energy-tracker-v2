using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
            IOptionsMonitor<OpenIdConnectOptions> oidcOptionsMonitor,
            CancellationToken cancellationToken) =>
        {
            var oidcOptions = oidcOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);
            var supportsFederatedLogout = await ResolveSupportsFederatedLogoutAsync(oidcOptions, cancellationToken);

            var householdId = householdAccessor.HouseholdId;
            if (householdId is null)
            {
                return Results.Ok(new SessionResponse(HasHousehold: false, HouseholdId: null, Locale: null, Currency: null, supportsFederatedLogout));
            }

            var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, cancellationToken);
            return Results.Ok(new SessionResponse(HasHousehold: true, household.Id, household.Locale, household.Currency, supportsFederatedLogout));
        });

        return api;
    }

    // FR-33/AC #3: reads the OIDC handler's own cached discovery document (the same
    // ConfigurationManager the login flow already populates — no extra network round trip) rather
    // than re-implementing discovery. When OIDC is unconfigured, the scheme was never registered
    // (Program.cs), so IOptionsMonitor.Get returns a default-constructed OpenIdConnectOptions whose
    // ConfigurationManager is null — that's the "logoff is unreachable anyway" case (Task 1).
    internal static async Task<bool> ResolveSupportsFederatedLogoutAsync(OpenIdConnectOptions options, CancellationToken cancellationToken)
    {
        if (options.ConfigurationManager is null)
        {
            return false;
        }

        var configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
        return !string.IsNullOrEmpty(configuration.EndSessionEndpoint);
    }
}

public record SessionResponse(bool HasHousehold, Guid? HouseholdId, string? Locale, string? Currency, bool SupportsFederatedLogout);
