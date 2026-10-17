using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.8 (AC #5; AD-17 FR-33, AD-27): the OIDC handler is told not to write its 302; the sign-out event
// hands the end-session URL to the /logout endpoint instead. The state protection the stock handler does
// after the event must be reproduced here, or the provider's confirmation lands on a blank page.
public class FederatedLogoutTests
{
    private static OpenIdConnectOptions Options() => new()
    {
        ClientId = "test-client-id",
        StateDataFormat = new PropertiesDataFormat(DataProtectionProvider.Create("federated-logout-tests").CreateProtector("state")),
    };

    private static RedirectContext ContextFor(OpenIdConnectOptions options, OpenIdConnectMessage message, HttpContext http)
    {
        var scheme = new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler));
        return new RedirectContext(http, scheme, options, new AuthenticationProperties { RedirectUri = "/" })
        {
            ProtocolMessage = message,
        };
    }

    [Fact]
    public void With_an_end_session_endpoint_it_captures_the_logout_url_and_protects_the_state()
    {
        var options = Options();
        var http = new DefaultHttpContext();
        var message = new OpenIdConnectMessage
        {
            IssuerAddress = "https://issuer.example/oidc/logout",
            ClientId = "test-client-id",
            PostLogoutRedirectUri = "https://app.example/signout-callback-oidc",
        };
        var context = ContextFor(options, message, http);

        FederatedLogout.Handle(context);

        context.Handled.ShouldBeTrue();
        var url = http.Items[FederatedLogout.RedirectUrlKey].ShouldBeOfType<string>();
        url.ShouldStartWith("https://issuer.example/oidc/logout");
        url.ShouldContain("client_id=test-client-id");
        url.ShouldContain("post_logout_redirect_uri=");

        var state = Uri.UnescapeDataString(url.Split("state=")[1].Split('&')[0]);
        state.ShouldNotBeNullOrEmpty();
        var unprotected = options.StateDataFormat.Unprotect(state);
        unprotected.ShouldNotBeNull();
        unprotected.RedirectUri.ShouldBe("/");
    }

    [Fact]
    public void A_state_already_on_the_message_is_kept_as_user_state_before_protection()
    {
        var options = Options();
        var http = new DefaultHttpContext();
        var message = new OpenIdConnectMessage { IssuerAddress = "https://issuer.example/oidc/logout", State = "caller-state" };

        FederatedLogout.Handle(ContextFor(options, message, http));

        var url = (string)http.Items[FederatedLogout.RedirectUrlKey]!;
        var state = Uri.UnescapeDataString(url.Split("state=")[1].Split('&')[0]);
        options.StateDataFormat.Unprotect(state)!.Items[OpenIdConnectDefaults.UserstatePropertiesKey].ShouldBe("caller-state");
    }

    [Fact]
    public void Without_an_end_session_endpoint_it_falls_back_to_a_local_sign_out()
    {
        var http = new DefaultHttpContext();
        var context = ContextFor(Options(), new OpenIdConnectMessage { IssuerAddress = string.Empty }, http);

        FederatedLogout.Handle(context);

        context.Handled.ShouldBeTrue();
        http.Items[FederatedLogout.RedirectUrlKey].ShouldBe("/");
    }

    [Fact]
    public async Task The_registered_event_still_sets_the_client_id_and_hands_the_url_over()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Oidc:Authority", "https://test-issuer.example");
            builder.UseSetting("Oidc:ClientId", "test-client-id");
            builder.UseSetting("Oidc:ClientSecret", "test-client-secret");
        });
        var registered = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        // The registered handlers, but a state format that needs no database-backed key ring.
        var http = new DefaultHttpContext();
        var message = new OpenIdConnectMessage { IssuerAddress = "https://issuer.example/oidc/logout" };

        await registered.Events.RedirectToIdentityProviderForSignOut(ContextFor(Options(), message, http));

        message.ClientId.ShouldBe("test-client-id");
        ((string)http.Items[FederatedLogout.RedirectUrlKey]!).ShouldContain("client_id=test-client-id");
    }
}
