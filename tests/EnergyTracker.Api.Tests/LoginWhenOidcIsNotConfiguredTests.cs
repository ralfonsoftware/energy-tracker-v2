using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.11 (AC #3, #8; audit C11): with no OIDC configured the scheme is never registered (deliberate,
// so the rest of the app still runs), and /login used to challenge that scheme anyway: an
// InvalidOperationException, a 500. It now answers 503 with an RFC 7807 problem document that tells the
// operator what to set. The configured path must be byte-for-byte what it was. The factory is the real
// Postgres-backed host (no OIDC by default) because the configured challenge writes a Data Protection
// protected correlation cookie, which needs the key ring in a database.
public class LoginWhenOidcIsNotConfiguredTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private const string AuthorizationEndpoint = "https://issuer.example/authorize";

    [Theory]
    [InlineData("/login")]
    [InlineData("/login?returnUrl=/join/abc")]
    public async Task GET_login_answers_503_problem_json_telling_the_operator_what_to_set(string url)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(503);
        document.RootElement.GetProperty("title").GetString().ShouldNotBeNullOrWhiteSpace();
        var detail = document.RootElement.GetProperty("detail").GetString()!;
        detail.ShouldContain("not configured");
        detail.ShouldContain("Oidc__Authority");
        detail.ShouldContain("Oidc__ClientId");
        detail.ShouldContain("Oidc__ClientSecret");
        detail.ShouldContain("docs/self-hosting.md");
        body.ShouldNotContain("Exception");
        body.ShouldNotContain("   at ", Case.Sensitive, "A problem document must not carry a stack trace.");
    }

    [Fact]
    public async Task GET_login_with_OIDC_configured_still_redirects_to_the_provider_with_the_same_client_id()
    {
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Oidc:Authority", "https://issuer.example");
            builder.UseSetting("Oidc:ClientId", "test-client-id");
            builder.UseSetting("Oidc:ClientSecret", "test-client-secret");
            builder.ConfigureTestServices(services =>
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration { AuthorizationEndpoint = AuthorizationEndpoint })));
        });
        using var client = configured.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/login?returnUrl=/join/abc", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.ShouldStartWith(AuthorizationEndpoint);
        location.ShouldContain("client_id=test-client-id");
    }
}
