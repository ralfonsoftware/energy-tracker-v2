using System.Security.Claims;
using EnergyTracker.Application;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdEndpoints
{
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

            try
            {
                var household = await createHousehold.ExecuteAsync(
                    issuerClaim.Value,
                    subjectClaim.Value,
                    request.Locale,
                    request.Currency,
                    cancellationToken);

                return Results.Ok(new HouseholdResponse(household.Id, household.Locale, household.Currency));
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

        return api;
    }
}

public record CreateHouseholdRequest(string Locale, string Currency);

public record HouseholdResponse(Guid Id, string Locale, string Currency);
