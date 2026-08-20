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

    public Guid? HouseholdId
    {
        get
        {
            if (!_resolved)
            {
                _householdId = Resolve();
                _resolved = true;
            }

            return _householdId;
        }
    }

    private Guid? Resolve()
    {
        // AD-3's job-processing resolution path: no HTTP request exists while a dequeued job
        // envelope is being processed, so there's nothing to read a principal from.
        if (httpContextAccessor.HttpContext is null)
        {
            return jobHouseholdContext.HouseholdId;
        }

        var user = httpContextAccessor.HttpContext?.User;
        var subjectClaim = user?.FindFirst(ClaimTypes.NameIdentifier);
        var issuerClaim = user?.FindFirst(HouseholdClaimTypes.ValidatedIssuer);
        if (subjectClaim is null || issuerClaim is null)
        {
            return null;
        }

        var issuer = issuerClaim.Value;
        var subject = subjectClaim.Value;

        using var dbContext = dbContextFactory.CreateDbContext();
        return dbContext.HouseholdMembers
            .Where(m => m.ExternalIssuer == issuer && m.ExternalSubjectId == subject)
            .Select(m => (Guid?)m.HouseholdId)
            .SingleOrDefault();
    }
}
