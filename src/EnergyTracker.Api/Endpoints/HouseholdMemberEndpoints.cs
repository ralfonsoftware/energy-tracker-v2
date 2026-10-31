using EnergyTracker.Application;
using EnergyTracker.Application.Ports;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdMemberEndpoints
{
    public static RouteGroupBuilder MapHouseholdMemberEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/household-members", async (
            ICurrentHouseholdAccessor householdAccessor,
            ListHouseholdMembers listHouseholdMembers,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.HouseholdId;
            if (householdId is null)
            {
                return NoHousehold();
            }

            var members = await listHouseholdMembers.ExecuteAsync(householdId.Value, cancellationToken);

            // The entity carries issuer and subject; they are dropped here and never leave the API (AD-25's reason).
            return Results.Ok(new HouseholdMembersResponse(members
                .Select(m => new HouseholdMemberResponse(m.Id, m.DisplayName, m.Id == householdAccessor.HouseholdMemberId, m.CreatedAtUtc))
                .ToList()));
        });

        // One route and one use case for removing another member and for leaving (the id is the caller's own).
        api.MapDelete("/household-members/{id:guid}", async (
            Guid id,
            ICurrentHouseholdAccessor householdAccessor,
            RemoveHouseholdMember removeHouseholdMember,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.HouseholdId;
            var actingMemberId = householdAccessor.HouseholdMemberId;
            if (householdId is null || actingMemberId is null)
            {
                return NoHousehold();
            }

            try
            {
                await removeHouseholdMember.ExecuteAsync(householdId.Value, actingMemberId.Value, id, cancellationToken);
                return Results.NoContent();
            }
            catch (HouseholdMemberNotFoundException)
            {
                // 404, never 403: another Household's member id must not be distinguishable from an unknown one (AD-26).
                return Problem("That member is not part of this Household.", "member-not-found", StatusCodes.Status404NotFound);
            }
            catch (LastHouseholdMemberException)
            {
                return Problem("A Household cannot be left without a member.", "last-member", StatusCodes.Status409Conflict);
            }
            catch (HouseholdConcurrencyConflictException)
            {
                return Problem("The members of this Household changed. Reload and try again.", "members-changed", StatusCodes.Status409Conflict);
            }
        }).RequireRateLimiting(RateLimitPolicies.MemberRemoval);

        return api;
    }

    internal static IResult NoHousehold() =>
        Results.Problem(
            detail: HouseholdRequiredFilter.RejectionDetail,
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult Problem(string detail, string errorCode, int statusCode) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode });
}

public record HouseholdMembersResponse(IReadOnlyList<HouseholdMemberResponse> Members);

public record HouseholdMemberResponse(Guid Id, string? DisplayName, bool IsCurrentMember, DateTimeOffset JoinedAtUtc);
