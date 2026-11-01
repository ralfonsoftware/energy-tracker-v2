using System.Text.RegularExpressions;
using EnergyTracker.Application;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.22 (AC #6; AD-26, ADR 0008): a new route with a {token} parameter cannot ship while the redactor leaves it readable.
public class InviteTokenRoutesAreRedactedTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static readonly Regex TokenParameter = new(@"\{token(:[^}]*)?\}", RegexOptions.None, TimeSpan.FromSeconds(1));

    // The SPA route is not an endpoint (MapFallbackToFile), so it is listed here.
    private const string SpaJoinPattern = "/join/{token}";

    // Not an invite: the import-confirm id is a one-time, Household-scoped GUID for a pending upload, not a join credential.
    private static readonly string[] NotInviteTokens = ["/api/household-import/{token:guid}/confirm"];

    private List<string> TokenRoutePatterns() =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(p => TokenParameter.IsMatch(p) && !NotInviteTokens.Contains(p))
            .Append(SpaJoinPattern)
            .Distinct()];

    private static List<string> Leaking(IEnumerable<string> patterns, Func<string, string> redact)
    {
        var token = InviteToken.Generate();
        return [.. patterns.Where(p => redact(TokenParameter.Replace(p, token)).Contains(token, StringComparison.Ordinal))];
    }

    [Fact]
    public void Every_route_with_a_token_parameter_is_redacted()
    {
        var patterns = TokenRoutePatterns();

        // Non-vacuous: the known invite routes are found.
        patterns.ShouldContain("/api/household-invites/{token}");
        patterns.ShouldContain("/api/household-invites/{token}/accept");
        patterns.ShouldContain(SpaJoinPattern);

        Leaking(patterns, InviteTokenRedactor.Redact)
            .ShouldBeEmpty("these routes carry {token} but InviteTokenRedactor leaves it readable (Story 11.22).");
    }

    [Fact]
    public void Known_bad_sample_an_identity_redactor_leaks_every_route()
    {
        var patterns = TokenRoutePatterns();

        Leaking(patterns, s => s).Count.ShouldBe(patterns.Count);
    }

    [Fact]
    public void Known_bad_sample_a_route_the_redactor_does_not_know_is_reported()
    {
        Leaking(["/api/invites-v2/{token}"], InviteTokenRedactor.Redact).ShouldBe(["/api/invites-v2/{token}"]);
    }
}
