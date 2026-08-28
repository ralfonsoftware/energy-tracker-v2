using System.Security.Claims;
using Shouldly;

namespace EnergyTracker.Application.Tests;

public class HouseholdClaimTypesTests
{
    [Fact]
    public void ResolveDisplayName_prefers_the_raw_name_claim_over_ClaimTypes_Name()
    {
        var user = PrincipalWithClaims(("name", "Raw Name"), (ClaimTypes.Name, "Mapped Name"));

        HouseholdClaimTypes.ResolveDisplayName(user).ShouldBe("Raw Name");
    }

    [Fact]
    public void ResolveDisplayName_falls_back_to_ClaimTypes_Name_when_the_raw_name_claim_is_absent()
    {
        var user = PrincipalWithClaims((ClaimTypes.Name, "Mapped Name"));

        HouseholdClaimTypes.ResolveDisplayName(user).ShouldBe("Mapped Name");
    }

    [Fact]
    public void ResolveDisplayName_falls_back_to_ClaimTypes_Name_when_the_raw_name_claim_is_an_empty_string()
    {
        var user = PrincipalWithClaims(("name", string.Empty), (ClaimTypes.Name, "Mapped Name"));

        HouseholdClaimTypes.ResolveDisplayName(user).ShouldBe("Mapped Name");
    }

    [Fact]
    public void ResolveDisplayName_returns_null_when_neither_claim_is_present()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        HouseholdClaimTypes.ResolveDisplayName(user).ShouldBeNull();
    }

    [Fact]
    public void ResolveDisplayName_returns_null_when_both_claims_are_empty_strings()
    {
        var user = PrincipalWithClaims(("name", string.Empty), (ClaimTypes.Name, string.Empty));

        HouseholdClaimTypes.ResolveDisplayName(user).ShouldBeNull();
    }

    private static ClaimsPrincipal PrincipalWithClaims(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)));
        return new ClaimsPrincipal(identity);
    }
}
