using System.Security.Claims;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdEndpoints
{
    private const string ForeignHouseholdDetail = "The authenticated principal does not belong to this Household.";

    // Household has no AD-3 query filter on itself (it's the tenant root), so every route taking a Household id
    // in the path must check it against the caller's resolved HouseholdId, or any principal could edit any Household.
    private static bool TryAuthorizeHousehold(Guid id, ICurrentHouseholdAccessor householdAccessor, out IResult? forbidden)
    {
        if (householdAccessor.HouseholdId is not { } ownHouseholdId)
        {
            forbidden = Results.Problem(detail: HouseholdRequiredFilter.RejectionDetail, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        if (ownHouseholdId != id)
        {
            forbidden = Results.Problem(detail: ForeignHouseholdDetail, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        forbidden = null;
        return true;
    }

    public static RouteGroupBuilder MapHouseholdEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/households", async (
            CreateHouseholdRequest request,
            ClaimsPrincipal user,
            CreateHousehold createHousehold,
            CancellationToken cancellationToken) =>
        {
            // Never a client-supplied identity — the creator is always the authenticated
            // principal behind the /api group's auth requirement.
            var subjectClaim = user.FindFirst(ClaimTypes.NameIdentifier);
            var issuerClaim = user.FindFirst(HouseholdClaimTypes.ValidatedIssuer);
            if (subjectClaim is null || issuerClaim is null)
            {
                return Results.Problem(
                    detail: "Authenticated principal is missing a required identity claim.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // The OIDC `name` claim, when the provider returns one: nullable, never fabricated, feeds the
            // job list's "Queued by" line. See HouseholdClaimTypes.ResolveDisplayName for why not ClaimTypes.Name.
            var displayName = HouseholdClaimTypes.ResolveDisplayName(user);

            try
            {
                var household = await createHousehold.ExecuteAsync(
                    issuerClaim.Value,
                    subjectClaim.Value,
                    request.Locale,
                    request.Currency,
                    displayName,
                    cancellationToken);

                return Results.Ok(ToDetailsResponse(household));
            }
            catch (HouseholdValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (HouseholdAlreadyExistsException ex)
            {
                // A principal that already has a HouseholdMember row must not be able to create a
                // second one via this endpoint — reject, don't silently no-op or duplicate.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/households/{id}", async (
            Guid id,
            ICurrentHouseholdAccessor householdAccessor,
            EnergyTrackerDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorizeHousehold(id, householdAccessor, out var forbidden))
            {
                return forbidden;
            }

            var household = await dbContext.Households.SingleAsync(h => h.Id == id, cancellationToken);
            return Results.Ok(ToDetailsResponse(household));
        });

        api.MapPut("/households/{id}/yearly-baseline", async (
            Guid id,
            SetYearlyBaselineRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            SetYearlyBaseline setYearlyBaseline,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorizeHousehold(id, householdAccessor, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                var household = await setYearlyBaseline.ExecuteAsync(id, request.YearlyBaselineKwh, request.Version, cancellationToken);
                return Results.Ok(ToDetailsResponse(household));
            }
            catch (HouseholdValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (HouseholdConcurrencyConflictException ex)
            {
                // Message only, not the full server state, like every other 409 here; the frontend's refetch covers current state.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // Any Household member may change the Household's language (no admin role in
        // v1). Unlike the routes above this carries no Version — see SetHouseholdLocale.
        api.MapPut("/households/{id}/locale", async (
            Guid id,
            SetHouseholdLocaleRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            SetHouseholdLocale setHouseholdLocale,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorizeHousehold(id, householdAccessor, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                var household = await setHouseholdLocale.ExecuteAsync(id, request.Locale, cancellationToken);
                return Results.Ok(ToDetailsResponse(household));
            }
            catch (HouseholdValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Always visible regardless of AiPlausibilityEnabled's value — GET never 404s or
        // omits the payload just because the toggle is off or the backend is unconfigured.
        api.MapGet("/households/{id}/ai-plausibility", async (
            Guid id,
            ICurrentHouseholdAccessor householdAccessor,
            EnergyTrackerDbContext dbContext,
            AiPlausibilityBackendOptions backendOptions,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorizeHousehold(id, householdAccessor, out var forbidden))
            {
                return forbidden;
            }

            var household = await dbContext.Households.SingleAsync(h => h.Id == id, cancellationToken);
            return Results.Ok(ToAiPlausibilityResponse(household, backendOptions));
        });

        api.MapPut("/households/{id}/ai-plausibility", async (
            Guid id,
            SetAiPlausibilityEnabledRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            SetAiPlausibilityEnabled setAiPlausibilityEnabled,
            AiPlausibilityBackendOptions backendOptions,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorizeHousehold(id, householdAccessor, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                var household = await setAiPlausibilityEnabled.ExecuteAsync(id, request.Enabled, request.Version, cancellationToken);
                return Results.Ok(ToAiPlausibilityResponse(household, backendOptions));
            }
            catch (HouseholdConcurrencyConflictException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        return api;
    }

    private static HouseholdResponse ToDetailsResponse(Household household) =>
        new(household.Id, household.Locale, household.Currency, household.YearlyBaselineKwh, household.Version);

    private static AiPlausibilitySettingsResponse ToAiPlausibilityResponse(Household household, AiPlausibilityBackendOptions backendOptions) =>
        new(household.AiPlausibilityEnabled, backendOptions.Configured, backendOptions.Label, household.Version);
}

public record CreateHouseholdRequest(string Locale, string Currency);

// One response shape for "a Household" everywhere it's returned (create, invite-accept, get,
// yearly-baseline update), so near-identical records cannot drift apart.
public record HouseholdResponse(Guid Id, string Locale, string Currency, decimal? YearlyBaselineKwh, int Version);

public record SetYearlyBaselineRequest(decimal YearlyBaselineKwh, int Version);

public record SetHouseholdLocaleRequest(string? Locale);

public record AiPlausibilitySettingsResponse(bool Enabled, bool BackendConfigured, string? BackendLabel, int Version);

public record SetAiPlausibilityEnabledRequest(bool Enabled, int Version);
