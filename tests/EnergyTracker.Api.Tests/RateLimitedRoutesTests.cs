using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.7 (AC #7; AD-27): the route-to-policy mapping is executable. A new upload, export or invite
// route cannot ship unthrottled, and a policy cannot be silently dropped from a route.
public class RateLimitedRoutesTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static readonly (string Method, string Route, string Policy)[] Expected =
    [
        ("GET", "/login", "Login"),
        ("POST", "/api/smart-plug-imports", "Upload"),
        ("POST", "/api/household-import", "Upload"),
        ("POST", "/api/household-import/{token:guid}/confirm", "Upload"),
        ("GET", "/api/household-export", "Export"),
        ("POST", "/api/household-invites", "InviteCreate"),
        ("GET", "/api/household-invites/{token}", "InviteRedeem"),
        ("POST", "/api/household-invites/{token}/accept", "InviteRedeem"),
    ];

    // Routes in the throttled families that are deliberately not limited (a single-use, id-addressed step).
    private static readonly HashSet<string> UnthrottledByDesign =
    [
        "POST /api/smart-plug-imports/{id:guid}/power-point-mapping",
    ];

    private static readonly string[] Families = ["household-invites", "household-import", "smart-plug-imports", "household-export"];

    private List<(string Method, string Route, string? Policy)> AllRoutes() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (
                    Method: method,
                    Route: endpoint.RoutePattern.RawText ?? string.Empty,
                    Policy: endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName)))
            .ToList();

    [Fact]
    public void Each_throttled_route_carries_its_policy()
    {
        var routes = AllRoutes();

        foreach (var (method, route, policy) in Expected)
        {
            routes.ShouldContain(
                r => r.Method == method && r.Route == route && r.Policy == policy,
                $"{method} {route} must carry rate-limit policy '{policy}'.");
        }
    }

    [Fact]
    public void Health_and_session_are_not_throttled()
    {
        var routes = AllRoutes();

        routes.Where(r => r.Route == "/health" || r.Route == "/api/session").ShouldNotBeEmpty();
        routes.Where(r => r.Route == "/health" || r.Route == "/api/session").ShouldAllBe(r => r.Policy == null);
    }

    [Fact]
    public void Every_route_in_a_throttled_family_is_classified()
    {
        var expectedKeys = Expected.Select(e => $"{e.Method} {e.Route}").ToHashSet();

        var unclassified = AllRoutes()
            .Where(r => Families.Any(f => r.Route.Contains(f, StringComparison.Ordinal)))
            .Select(r => $"{r.Method} {r.Route}")
            .Where(key => !expectedKeys.Contains(key) && !UnthrottledByDesign.Contains(key))
            .ToList();

        unclassified.ShouldBeEmpty(
            "Add each new route in these families to the rate-limit table (story 11.7) and to Expected, or list it as unthrottled by design.");
    }
}
