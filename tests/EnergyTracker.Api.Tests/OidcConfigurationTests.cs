using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace EnergyTracker.Api.Tests;

public class OidcConfigurationTests
{
    [Fact]
    public async Task Requests_the_email_scope_when_OIDC_is_configured()
    {
        // Regression guard for Story 8.1/Task 5's live-discovered bug: Auth0 omits the email
        // claim entirely (from both the ID token and the userinfo response) unless the "email"
        // scope is explicitly requested — a future accidental removal of Program.cs's
        // `options.Scope.Add("email")` would otherwise only surface via another live Auth0
        // session, since TestAuthHandler bypasses real OIDC scope negotiation entirely.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Oidc:Authority", "https://test-issuer.example");
            builder.UseSetting("Oidc:ClientId", "test-client-id");
            builder.UseSetting("Oidc:ClientSecret", "test-client-secret");
        });

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        options.Scope.ShouldContain("email");
    }
}
