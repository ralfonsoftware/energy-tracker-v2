using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GET_health_returns_200_OK()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
