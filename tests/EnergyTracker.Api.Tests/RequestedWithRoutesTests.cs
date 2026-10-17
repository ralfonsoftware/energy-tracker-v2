using EnergyTracker.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.8 (AC #7; AD-27, audit S9): the CSRF header requirement is executable. A new mutation route
// under /api cannot ship without the filter, and /logout is a POST that needs it too.
public class RequestedWithRoutesTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static readonly HashSet<string> MutatingMethods = ["POST", "PUT", "PATCH", "DELETE"];

    private List<(string Method, string Route, bool Protected)> AllRoutes() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => (
                    Method: method,
                    Route: endpoint.RoutePattern.RawText ?? string.Empty,
                    Protected: endpoint.Metadata.GetMetadata<RequestedWithRequiredMetadata>() is not null)))
            .ToList();

    [Fact]
    public void Every_mutation_route_under_api_and_POST_logout_carries_the_marker()
    {
        var mutations = AllRoutes()
            .Where(r => (r.Route.StartsWith("/api", StringComparison.Ordinal) && MutatingMethods.Contains(r.Method))
                        || (r.Method == "POST" && r.Route == "/logout"))
            .ToList();

        // Non-vacuous: the scan must actually have found the known members.
        mutations.Count.ShouldBeGreaterThan(20);
        mutations.ShouldContain(r => r.Method == "POST" && r.Route == "/api/rooms");
        mutations.ShouldContain(r => r.Method == "DELETE" && r.Route == "/api/rooms/{id}");
        mutations.ShouldContain(r => r.Method == "PUT" && r.Route == "/api/households/{id}/locale");
        mutations.ShouldContain(r => r.Method == "POST" && r.Route == "/api/smart-plug-imports");
        mutations.ShouldContain(r => r.Method == "POST" && r.Route == "/logout");

        var unprotected = mutations.Where(r => !r.Protected).Select(r => $"{r.Method} {r.Route}").ToList();
        unprotected.ShouldBeEmpty("These mutation routes lack RequireRequestedWith (story 11.8, AD-27).");
    }

    [Fact]
    public void GET_logout_no_longer_exists()
    {
        AllRoutes().ShouldNotContain(r => r.Method == "GET" && r.Route == "/logout");
    }

    [Fact]
    public void Health_and_every_GET_carry_no_marker()
    {
        var routes = AllRoutes();

        routes.Where(r => r.Route == "/health").ShouldNotBeEmpty();
        routes.Where(r => r.Method == "GET").ShouldNotBeEmpty();
        routes.Where(r => r.Method is "GET" or "HEAD").ShouldAllBe(r => !r.Protected);
    }
}
