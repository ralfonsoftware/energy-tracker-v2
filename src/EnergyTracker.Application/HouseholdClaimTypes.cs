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
}
