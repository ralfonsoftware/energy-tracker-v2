using System.Security.Claims;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace EnergyTracker.Infrastructure.Adapters;

/// <summary>
/// Resolves the current authenticated principal's Household by looking up a HouseholdMember
/// row keyed on the OIDC issuer+subject carried by the cookie principal (AD-3's HTTP-request
/// resolution path). "Does this principal have a Household" is the only question asked here —
/// never "does any Household exist system-wide" (a deployment may legitimately hold more than one).
/// Queries through HouseholdMembershipDbContext (see its own doc comment) rather than
/// EnergyTrackerDbContext directly, so EnergyTrackerDbContext can take this accessor as a normal
/// constructor dependency with no circular-DI workaround needed.
/// </summary>
public class CurrentHouseholdAccessor(
    IHttpContextAccessor httpContextAccessor,
    IDbContextFactory<HouseholdMembershipDbContext> dbContextFactory,
    JobHouseholdContext jobHouseholdContext)
    : ICurrentHouseholdAccessor
{
    private bool _resolved;
    private Guid? _householdId;
    private Guid? _householdMemberId;

    public Guid? HouseholdId
    {
        get
        {
            EnsureResolved();
            return _householdId;
        }
    }

    public Guid? HouseholdMemberId
    {
        get
        {
            EnsureResolved();
            return _householdMemberId;
        }
    }

    private void EnsureResolved()
    {
        if (_resolved)
        {
            return;
        }

        (_householdMemberId, _householdId) = Resolve();
        _resolved = true;
    }

    private (Guid? MemberId, Guid? HouseholdId) Resolve()
    {
        // AD-3's job-processing resolution path: no HTTP request exists while a dequeued job
        // envelope is being processed, so there's nothing to read a principal from. No
        // HouseholdMember concept applies to a job-processing scope either.
        if (httpContextAccessor.HttpContext is null)
        {
            return (null, jobHouseholdContext.HouseholdId);
        }

        var user = httpContextAccessor.HttpContext?.User;
        var subjectClaim = user?.FindFirst(ClaimTypes.NameIdentifier);
        var issuerClaim = user?.FindFirst(HouseholdClaimTypes.ValidatedIssuer);
        if (subjectClaim is null || issuerClaim is null)
        {
            return (null, null);
        }

        var issuer = issuerClaim.Value;
        var subject = subjectClaim.Value;

        using var dbContext = dbContextFactory.CreateDbContext();
        // Selects both the member's own id and its HouseholdId in one query — same cost as the
        // single-field projection this replaces (Story 3.6/AD-6 extension). Projects into an
        // anonymous type, not a ValueTuple directly — Npgsql can't translate/read a server-side
        // tuple projection (it tries to read it back as a Postgres composite "record" type and
        // throws NotSupportedException); converting to a tuple happens client-side afterward.
        var result = dbContext.HouseholdMembers
            .Where(m => m.ExternalIssuer == issuer && m.ExternalSubjectId == subject)
            .Select(m => new { MemberId = (Guid?)m.Id, HouseholdId = (Guid?)m.HouseholdId })
            .SingleOrDefault();

        return result is null ? (null, null) : (result.MemberId, result.HouseholdId);
    }
}
