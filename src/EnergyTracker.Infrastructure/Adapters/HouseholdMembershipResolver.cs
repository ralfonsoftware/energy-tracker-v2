using System.Security.Claims;
using EnergyTracker.Application;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

/// <summary>
/// Looks up the authenticated principal's HouseholdMember row by the validated OIDC issuer and subject
/// (AD-3's HTTP-request resolution path), asynchronously and once per request: the membership middleware
/// calls it, the accessor only reads the cached result (audit C2). "Does this principal have a Household"
/// is the only question asked here, never "does any Household exist system-wide". Queries through
/// HouseholdMembershipDbContext (see its own doc comment), so it never nests inside a query on
/// EnergyTrackerDbContext.
/// </summary>
public class HouseholdMembershipResolver(IDbContextFactory<HouseholdMembershipDbContext> dbContextFactory)
{
    // Virtual only so the middleware can be unit-tested with a substitute (the interface for it would have to
    // live in Application, which keeps the accessor port as its single household port, AD-1).
    public virtual async Task<ResolvedHouseholdMembership> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var subjectClaim = user.FindFirst(ClaimTypes.NameIdentifier);
        var issuerClaim = user.FindFirst(HouseholdClaimTypes.ValidatedIssuer);
        if (subjectClaim is null || issuerClaim is null)
        {
            return ResolvedHouseholdMembership.None;
        }

        var issuer = issuerClaim.Value;
        var subject = subjectClaim.Value;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Selects both the member's own id and its HouseholdId in one query — same cost as the
        // single-field projection this replaces (Story 3.6/AD-6 extension). Projects into an
        // anonymous type, not a ValueTuple directly — Npgsql can't translate/read a server-side
        // tuple projection (it tries to read it back as a Postgres composite "record" type and
        // throws NotSupportedException); converting to the record happens client-side afterward.
        var result = await dbContext.HouseholdMembers
            .Where(m => m.ExternalIssuer == issuer && m.ExternalSubjectId == subject)
            .Select(m => new { MemberId = (Guid?)m.Id, HouseholdId = (Guid?)m.HouseholdId })
            .SingleOrDefaultAsync(cancellationToken);

        return result is null ? ResolvedHouseholdMembership.None : new ResolvedHouseholdMembership(result.MemberId, result.HouseholdId);
    }
}
