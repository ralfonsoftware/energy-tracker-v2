using System.Security.Claims;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using Microsoft.AspNetCore.Http;

namespace EnergyTracker.Infrastructure.Adapters;

/// <summary>
/// Reads the current authenticated principal's Household (AD-3's HTTP-request resolution path). The lookup
/// itself runs once per request, asynchronously, in HouseholdMembershipMiddleware, which caches the result
/// in HttpContext.Items; this class only reads it, so the EF query filter and the endpoints never block a
/// thread on a database round trip (audit C2). "Does this principal have a Household" is the only question
/// asked — never "does any Household exist system-wide" (a deployment may legitimately hold more than one).
/// </summary>
public class CurrentHouseholdAccessor(
    IHttpContextAccessor httpContextAccessor,
    JobHouseholdContext jobHouseholdContext)
    : ICurrentHouseholdAccessor
{
    public Guid? HouseholdId => Current().HouseholdId;

    public Guid? HouseholdMemberId => Current().MemberId;

    // Lazy on purpose: Data Protection builds an EnergyTrackerDbContext (and so this accessor) inside
    // UseAuthentication, before the middleware has run, so nothing may resolve in the constructor.
    private ResolvedHouseholdMembership Current()
    {
        // AD-3's job-processing resolution path: no HTTP request exists while a dequeued job
        // envelope is being processed, so there's nothing to read a principal from. No
        // HouseholdMember concept applies to a job-processing scope either.
        var http = httpContextAccessor.HttpContext;
        if (http is null)
        {
            return new ResolvedHouseholdMembership(null, jobHouseholdContext.HouseholdId);
        }

        if (ResolvedHouseholdMembership.TryGet(http, out var cached))
        {
            return cached;
        }

        // Anonymous or claim-less: there is nobody to look up.
        if (http.User.FindFirst(ClaimTypes.NameIdentifier) is null
            || http.User.FindFirst(HouseholdClaimTypes.ValidatedIssuer) is null)
        {
            return ResolvedHouseholdMembership.None;
        }

        // A real principal with nothing cached is a wiring bug (a route the middleware does not cover).
        // Never fall back to a synchronous lookup and never answer "no Household": either hides it.
        throw new InvalidOperationException(
            "The Household of this authenticated request was not resolved: the middleware did not run for it. " +
            "UseHouseholdMembership covers paths under /api only.");
    }
}
