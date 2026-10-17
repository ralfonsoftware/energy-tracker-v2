using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.8 (AC #5; Task 3.4): the real OpenIdConnectHandler, with a static discovery document in place of
// a provider, behind POST /logout. This is what proves the framework facts the story relies on: the handler
// builds the end-session URL, skips its own redirect when the event handles it, throws without an
// end_session_endpoint (hence the explicit fallback), and /signout-callback-oidc needs the protected state
// to send the member back to "/". The live Auth0 round-trip remains the Task 8.2 / Part R gate.
public class FederatedLogoutEndToEndTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private const string EndSessionEndpoint = "https://issuer.example/oidc/logout";

    private HttpClient ClientWith(OpenIdConnectConfiguration discovery) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Oidc:Authority", "https://issuer.example");
            builder.UseSetting("Oidc:ClientId", "test-client-id");
            builder.UseSetting("Oidc:ClientSecret", "test-client-secret");
            builder.ConfigureTestServices(services =>
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery)));
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage Logout()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/logout");
        request.Headers.Add("X-Requested-With", "fetch");
        return request;
    }

    private sealed record LogoutBody(string RedirectUrl);

    [Fact]
    public async Task POST_logout_returns_the_providers_end_session_url_without_a_redirect()
    {
        using var client = ClientWith(new OpenIdConnectConfiguration { EndSessionEndpoint = EndSessionEndpoint });

        using var response = await client.SendAsync(Logout(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Location.ShouldBeNull();
        var url = (await response.Content.ReadFromJsonAsync<LogoutBody>(TestContext.Current.CancellationToken))!.RedirectUrl;
        url.ShouldStartWith(EndSessionEndpoint);
        url.ShouldContain("client_id=test-client-id");
        url.ShouldContain("post_logout_redirect_uri=" + Uri.EscapeDataString("http://localhost/signout-callback-oidc"));
        url.ShouldContain("state=");
    }

    [Fact]
    public async Task The_returned_state_sends_the_member_back_to_the_app_after_the_providers_confirmation()
    {
        using var client = ClientWith(new OpenIdConnectConfiguration { EndSessionEndpoint = EndSessionEndpoint });
        using var logout = await client.SendAsync(Logout(), TestContext.Current.CancellationToken);
        var url = (await logout.Content.ReadFromJsonAsync<LogoutBody>(TestContext.Current.CancellationToken))!.RedirectUrl;
        var state = url.Split("state=")[1].Split('&')[0];

        using var callback = await client.GetAsync($"/signout-callback-oidc?state={state}", TestContext.Current.CancellationToken);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.ShouldBe("/");
    }

    [Fact]
    public async Task A_provider_without_an_end_session_endpoint_degrades_to_a_local_sign_out_instead_of_a_500()
    {
        using var client = ClientWith(new OpenIdConnectConfiguration());

        using var response = await client.SendAsync(Logout(), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LogoutBody>(TestContext.Current.CancellationToken))!.RedirectUrl.ShouldBe("/");
    }
}
