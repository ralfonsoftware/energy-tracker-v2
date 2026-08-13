using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace EnergyTracker.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, bool oidcConfigured)
    {
        // Issues Results.Challenge for the OIDC scheme, redirecting the browser to the
        // configured provider (AC #1). Outside /api — this is a full page navigation, not
        // something the SPA calls via fetch. Meaningless without a real provider configured —
        // /login is the one endpoint this story's Program.cs comment explicitly accepts as
        // unusable until Oidc:Authority/Oidc:ClientId are set.
        app.MapGet("/login", () =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                [OpenIdConnectDefaults.AuthenticationScheme]));

        // Signs out the cookie scheme, plus the OIDC scheme (provider-side end-session, if
        // supported) only when it's actually registered — /logout is reachable by anyone,
        // unauthenticated, at any time, unlike /login, so it must not throw when OIDC is still
        // unconfigured (the expected state before a self-hoster sets up a real provider).
        app.MapGet("/logout", () =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/" },
                oidcConfigured
                    ? [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
                    : [CookieAuthenticationDefaults.AuthenticationScheme]));

        return app;
    }
}
