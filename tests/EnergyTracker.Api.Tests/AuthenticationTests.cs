using System.Net;
using System.Net.Http.Json;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class AuthenticationTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    [Fact]
    public async Task GET_api_session_without_authentication_returns_401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/session", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task POST_api_households_without_authentication_returns_401()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/households", new { locale = "de-DE", currency = "EUR" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_health_stays_unauthenticated_even_though_api_requires_auth()
    {
        // Regression guard (AD-19): Story 1.1 already established /health unauthenticated —
        // AC #5's "any route... except the OIDC callback" must not flip this to 401.
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GET_the_SPA_shell_stays_reachable_unauthenticated()
    {
        // The browser must be able to load the app shell itself before any login can happen.
        var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
