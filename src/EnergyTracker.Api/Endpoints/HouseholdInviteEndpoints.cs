using System.Security.Claims;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdInviteEndpoints
{
    public static RouteGroupBuilder MapHouseholdInviteEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/household-invites", async (
            ICurrentHouseholdAccessor householdAccessor,
            CreateHouseholdInvite createHouseholdInvite,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.HouseholdId;
            if (householdId is null)
            {
                // Authenticated but no Household yet — can't invite people into a Household
                // you're not in.
                return Results.Problem(
                    detail: "The authenticated principal does not belong to a Household.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var invite = await createHouseholdInvite.ExecuteAsync(householdId.Value, cancellationToken);

            return Results.Ok(new HouseholdInviteResponse(invite.Token, invite.ExpiresAtUtc));
        });

        // Side-effect-free preview/validity check — this endpoint must never consume the
        // invite. Correct REST semantics for a GET, and defense in depth: the route sits behind
        // RequireAuthorization() so an anonymous link-preview bot can't reach it today, but a
        // GET with a side effect would be one auth-policy change away from letting exactly that
        // scenario silently burn a single-use invite before the real person clicks anything.
        api.MapGet("/household-invites/{token}", async (
            string token,
            IHouseholdRepository repository,
            CancellationToken cancellationToken) =>
        {
            var invite = await repository.FindInviteByTokenAsync(token, cancellationToken);
            if (invite is null)
            {
                return Results.NotFound();
            }

            if (invite.ConsumedAtUtc is not null || invite.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                return Results.Problem(
                    detail: "This invite is no longer valid.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(new HouseholdInvitePreviewResponse(invite.ExpiresAtUtc));
        });

        api.MapPost("/household-invites/{token}/accept", async (
            string token,
            ClaimsPrincipal user,
            AcceptHouseholdInvite acceptHouseholdInvite,
            CancellationToken cancellationToken) =>
        {
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
                var household = await acceptHouseholdInvite.ExecuteAsync(
                    token,
                    issuerClaim.Value,
                    subjectClaim.Value,
                    cancellationToken);

                return Results.Ok(new HouseholdResponse(household.Id, household.Locale, household.Currency));
            }
            catch (HouseholdInviteNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (HouseholdInviteExpiredOrConsumedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (HouseholdAlreadyExistsException ex)
            {
                // Reuses the exact status code POST /households already uses for the same
                // underlying "this principal already has a Household" condition.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        return api;
    }
}

public record HouseholdInviteResponse(string Token, DateTimeOffset ExpiresAtUtc);

public record HouseholdInvitePreviewResponse(DateTimeOffset ExpiresAtUtc);
