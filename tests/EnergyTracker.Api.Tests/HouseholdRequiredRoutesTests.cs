using System.Net;
using System.Net.Http.Json;
using EnergyTracker.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.19 (AC #6; audit R1): the Household guard is executable. A new route under /api cannot ship
// without RequireHousehold() unless it is named here as a route that works for a caller with no Household.
public class HouseholdRequiredRoutesTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    // Routes that legitimately work without a Household: they create one, accept an invite into one, report
    // the caller's session, or do their own check (the id in the path against the caller's Household).
    private static readonly HashSet<string> WorksWithoutHousehold =
    [
        "GET /api/session",
        "POST /api/households",
        "GET /api/households/{id}",
        "PUT /api/households/{id}/yearly-baseline",
        "PUT /api/households/{id}/locale",
        "GET /api/households/{id}/ai-plausibility",
        "PUT /api/households/{id}/ai-plausibility",
        "POST /api/household-invites",
        "GET /api/household-invites",
        "DELETE /api/household-invites/{id:guid}",
        "GET /api/household-invites/{token}",
        "POST /api/household-invites/{token}/accept",
        "GET /api/household-members",
        "DELETE /api/household-members/{id:guid}",
    ];

    private List<(string Key, bool Marked)> ApiRoutes() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => IsApiRoute(endpoint.RoutePattern.RawText))
            .SelectMany(endpoint => MethodsOf(endpoint)
                .Select(method => (
                    Key: $"{method} {endpoint.RoutePattern.RawText}",
                    Marked: endpoint.Metadata.GetMetadata<HouseholdRequiredMetadata>() is not null)))
            .ToList();

    private static bool IsApiRoute(string? rawText) =>
        rawText == "/api" || rawText?.StartsWith("/api/", StringComparison.Ordinal) == true;

    // A route without HTTP method metadata answers every method: it is listed as ANY, never skipped.
    private static IEnumerable<string> MethodsOf(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return methods is { Count: > 0 } ? methods : ["ANY"];
    }

    [Fact]
    public void Every_route_under_api_is_behind_RequireHousehold_or_named_in_the_allowlist()
    {
        var routes = ApiRoutes();

        // Non-vacuous: the sweep must actually have found the known members of both sides.
        routes.Count.ShouldBeGreaterThan(40);
        routes.ShouldContain(r => r.Key == "POST /api/rooms" && r.Marked);
        routes.ShouldContain(r => r.Key == "GET /api/status" && r.Marked);
        routes.ShouldContain(r => r.Key == "GET /api/jobs/{id:guid}" && r.Marked);
        routes.ShouldContain(r => r.Key == "POST /api/households" && !r.Marked);

        routes.Select(r => r.Key).Distinct().Count().ShouldBe(routes.Count, "Two /api routes share one method and pattern.");

        var unguarded = routes.Where(r => !r.Marked && !WorksWithoutHousehold.Contains(r.Key)).Select(r => r.Key).ToList();
        unguarded.ShouldBeEmpty("These /api routes lack RequireHousehold() and are not in the allowlist (Story 11.19, audit R1).");
    }

    [Fact]
    public void The_allowlist_names_no_route_that_is_marked_or_missing()
    {
        var routes = ApiRoutes();

        WorksWithoutHousehold.Except(routes.Select(r => r.Key)).ShouldBeEmpty("The allowlist names routes that no longer exist: remove them.");
        routes.Where(r => r.Marked && WorksWithoutHousehold.Contains(r.Key)).ShouldBeEmpty("A marked route is also allowlisted: remove it from the allowlist.");
    }

    [Theory]
    [InlineData("GET", "/api/rooms")]
    [InlineData("GET", "/api/status")]
    public async Task A_marked_route_answers_403_to_a_caller_without_a_Household(string method, string path)
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Mapping_a_Smart_Plug_import_without_a_Household_is_403_not_404()
    {
        var client = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync(
            $"/api/smart-plug-imports/{Guid.NewGuid()}/power-point-mapping",
            new { powerPointId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
