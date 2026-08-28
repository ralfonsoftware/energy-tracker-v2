using System.Security.Claims;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using EnergyTracker.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";
    private const string ForeignHouseholdDetail = "The authenticated principal does not belong to this Household.";

    // Household has no AD-3 query filter on itself (it's the tenant root), so every route below
    // that takes a Household id in the path must check it against the caller's own resolved
    // HouseholdId explicitly — otherwise any authenticated principal could read or edit any
    // other Household's Yearly Baseline just by guessing/knowing its id.
    private static bool TryAuthorizeHousehold(Guid id, ICurrentHouseholdAccessor householdAccessor, out IResult? forbidden)
    {
        if (householdAccessor.HouseholdId is not { } ownHouseholdId)
        {
            forbidden = Results.Problem(detail: NoHouseholdDetail, statusCode: StatusCodes.Status403Forbidden);
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
            // Never a client-supplied identity (AC #2) — the creator is always the authenticated
            // principal behind the /api group's auth requirement.
            var subjectClaim = user.FindFirst(ClaimTypes.NameIdentifier);
            var issuerClaim = user.FindFirst(HouseholdClaimTypes.ValidatedIssuer);
            if (subjectClaim is null || issuerClaim is null)
            {
                return Results.Problem(
                    detail: "Authenticated principal is missing a required identity claim.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Story 3.6/UX-DR21: the OIDC `name` claim, when the provider returns one — nullable,
            // never fabricated, feeds the household-wide job list's "Queued by" line. See
            // HouseholdClaimTypes.ResolveDisplayName for why this isn't a plain ClaimTypes.Name
            // read (review-round-2 patch).
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
                // Message only, not the full current server state — matches every existing 409
                // in this codebase (HouseholdAlreadyExistsException, tagging-scaffold archived-
                // parent conflicts); the frontend's own refetch covers getting the current state.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        return api;
    }

    private static HouseholdResponse ToDetailsResponse(Household household) =>
        new(household.Id, household.Locale, household.Currency, household.YearlyBaselineKwh, household.Version);
}

public record CreateHouseholdRequest(string Locale, string Currency);

// One response shape for "a Household" everywhere it's returned (create, invite-accept, get,
// yearly-baseline update) — avoids two near-identical records drifting independently as later
// stories add fields.
public record HouseholdResponse(Guid Id, string Locale, string Currency, decimal? YearlyBaselineKwh, int Version);

public record SetYearlyBaselineRequest(decimal YearlyBaselineKwh, int Version);
