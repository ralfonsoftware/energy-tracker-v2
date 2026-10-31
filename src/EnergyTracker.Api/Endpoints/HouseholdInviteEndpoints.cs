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
                // Authenticated but no Household yet: cannot invite into a Household you are not in.
                return Results.Problem(
                    detail: HouseholdRequiredFilter.RejectionDetail,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var invite = await createHouseholdInvite.ExecuteAsync(householdId.Value, cancellationToken);

            return Results.Ok(new HouseholdInviteResponse(invite.Token, invite.ExpiresAtUtc));
        }).RequireRateLimiting(RateLimitPolicies.InviteCreate);

        // Open invites only: never a token or a hash (only the hash is stored, so a new invite's link cannot be shown again).
        api.MapGet("/household-invites", async (
            ICurrentHouseholdAccessor householdAccessor,
            ListOpenHouseholdInvites listOpenHouseholdInvites,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.HouseholdId;
            if (householdId is null)
            {
                return HouseholdMemberEndpoints.NoHousehold();
            }

            var invites = await listOpenHouseholdInvites.ExecuteAsync(householdId.Value, cancellationToken);

            return Results.Ok(new OpenHouseholdInvitesResponse(invites
                .Select(i => new OpenHouseholdInviteResponse(i.Id, i.CreatedAtUtc, i.ExpiresAtUtc))
                .ToList()));
        });

        api.MapDelete("/household-invites/{id:guid}", async (
            Guid id,
            ICurrentHouseholdAccessor householdAccessor,
            RevokeHouseholdInvite revokeHouseholdInvite,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.HouseholdId;
            if (householdId is null)
            {
                return HouseholdMemberEndpoints.NoHousehold();
            }

            try
            {
                await revokeHouseholdInvite.ExecuteAsync(householdId.Value, id, cancellationToken);
                return Results.NoContent();
            }
            catch (HouseholdInviteNotOpenException)
            {
                // Unknown, another Household's, consumed, expired and already revoked all answer the same.
                return Results.Problem(
                    detail: "That invite is not open.",
                    statusCode: StatusCodes.Status404NotFound,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = "invite-not-found" });
            }
        }).RequireRateLimiting(RateLimitPolicies.MemberRemoval);

        // Side-effect-free preview: a GET must never consume the invite. Defense in depth, since a link-preview
        // bot reaching a consuming GET would burn the single-use invite before the real person clicks.
        api.MapGet("/household-invites/{token}", async (
            string token,
            IHouseholdRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var invite = InviteToken.IsPlausible(token)
                ? await repository.FindInviteByTokenAsync(token, cancellationToken)
                : null;
            if (invite is null)
            {
                return Results.NotFound();
            }

            if (invite.ConsumedAtUtc is not null || invite.ExpiresAtUtc <= timeProvider.GetUtcNow())
            {
                return Results.Problem(
                    detail: "This invite is no longer valid.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(new HouseholdInvitePreviewResponse(invite.ExpiresAtUtc));
        }).RequireRateLimiting(RateLimitPolicies.InviteRedeem);

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

            // Same nullable OIDC `name` claim as HouseholdEndpoints' POST /households (see HouseholdClaimTypes.ResolveDisplayName).
            var displayName = HouseholdClaimTypes.ResolveDisplayName(user);

            try
            {
                var household = await acceptHouseholdInvite.ExecuteAsync(
                    token,
                    issuerClaim.Value,
                    subjectClaim.Value,
                    displayName,
                    cancellationToken);

                return Results.Ok(new HouseholdResponse(household.Id, household.Locale, household.Currency, household.YearlyBaselineKwh, household.Version));
            }
            catch (HouseholdInviteNotFoundException ex)
            {
                // The exceptions carry no token, so nothing the caller presented is echoed back.
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (HouseholdInviteExpiredOrConsumedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (HouseholdAlreadyExistsException ex)
            {
                // Same status code POST /households uses for "this principal already has a Household".
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).RequireRateLimiting(RateLimitPolicies.InviteRedeem);

        return api;
    }
}

public record HouseholdInviteResponse(string Token, DateTimeOffset ExpiresAtUtc);

public record HouseholdInvitePreviewResponse(DateTimeOffset ExpiresAtUtc);

public record OpenHouseholdInvitesResponse(IReadOnlyList<OpenHouseholdInviteResponse> Invites);

public record OpenHouseholdInviteResponse(Guid Id, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc);
