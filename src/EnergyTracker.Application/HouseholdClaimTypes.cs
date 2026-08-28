using System.Security.Claims;

namespace EnergyTracker.Application;

/// <summary>Well-known claim types used to resolve a HouseholdMember's issuer+subject identity.</summary>
public static class HouseholdClaimTypes
{
    /// <summary>
    /// The OIDC provider's validated issuer, captured explicitly from the ID token at
    /// OnTokenValidated time (see Program.cs) rather than read back from the ambient
    /// Claim.Issuer of a NameIdentifier claim — GetClaimsFromUserInfoEndpoint's later
    /// claim-merge step is not guaranteed to preserve the original validated issuer on every
    /// claim, and issuer+subject is the entire basis for this app's tenant isolation.
    /// </summary>
    public const string ValidatedIssuer = "household_external_issuer";

    /// <summary>
    /// Resolves a display name from the OIDC <c>name</c> claim (Story 3.6, review-round-2 patch).
    /// Checked under its raw JSON claim type first — Program.cs sets
    /// <c>GetClaimsFromUserInfoEndpoint = true</c> with no explicit ClaimActions mapping, so
    /// userinfo-sourced claims keep their raw JSON key rather than being remapped to
    /// <see cref="ClaimTypes.Name"/> the way ID-token claims are via the JWT handler's default
    /// inbound map — confirmed empirically against a live Auth0 test-user session (this story's
    /// Review Findings). Falls back to <see cref="ClaimTypes.Name"/> for an IdP configuration
    /// that does map it. An empty-string claim value is treated the same as absent — never
    /// fabricated.
    /// </summary>
    public static string? ResolveDisplayName(ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value is { Length: > 0 } rawName
            ? rawName
            : user.FindFirst(ClaimTypes.Name)?.Value is { Length: > 0 } mappedName
                ? mappedName
                : null;
}
