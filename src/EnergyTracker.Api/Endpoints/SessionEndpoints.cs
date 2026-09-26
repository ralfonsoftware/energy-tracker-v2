using System.Security.Claims;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EnergyTracker.Api.Endpoints;

public static class SessionEndpoints
{
    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder api)
    {
        // Singleton resource, not a collection — /api/session, not plural (consistent with /health).
        // Sits behind the /api group's auth requirement, so an unauthenticated call 401s; the SPA's
        // response to that 401 is what triggers navigation to /login (AC #1).
        api.MapGet("/session", async (
            ClaimsPrincipal user,
            ICurrentHouseholdAccessor householdAccessor,
            EnergyTrackerDbContext dbContext,
            IOptionsMonitor<OpenIdConnectOptions> oidcOptionsMonitor,
            CancellationToken cancellationToken) =>
        {
            var oidcOptions = oidcOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);
            var supportsFederatedLogout = await ResolveSupportsFederatedLogoutAsync(oidcOptions, cancellationToken);
            // Story 8.1/AC #3: read from claims at request time, never persisted — the same
            // "don't duplicate identity" discipline ResolveDisplayName already follows.
            var email = HouseholdClaimTypes.ResolveEmail(user);

            var householdId = householdAccessor.HouseholdId;
            if (householdId is null)
            {
                return Results.Ok(new SessionResponse(HasHousehold: false, HouseholdId: null, Locale: null, Currency: null, supportsFederatedLogout, email));
            }

            var household = await dbContext.Households.SingleAsync(h => h.Id == householdId, cancellationToken);
            return Results.Ok(new SessionResponse(HasHousehold: true, household.Id, household.Locale, household.Currency, supportsFederatedLogout, email));
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

        // A discovery fetch failure (IdP unreachable, e.g. on cold start before any successful
        // fetch populates the cache) must not fail the whole /api/session request — it only means
        // this one signal degrades to "unsupported," the same graceful fallback AC #3 already
        // requires for a provider that genuinely lacks RP-initiated logout.
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(configuration.EndSessionEndpoint);
    }
}

public record SessionResponse(bool HasHousehold, Guid? HouseholdId, string? Locale, string? Currency, bool SupportsFederatedLogout, string? Email);
