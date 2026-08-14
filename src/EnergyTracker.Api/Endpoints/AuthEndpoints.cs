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
        // returnUrl (Story 1.8) lets the invited person's very first click — /join/{token} —
        // survive the OIDC round trip instead of always landing on "/".
        app.MapGet("/login", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = IsSafeLocalReturnUrl(returnUrl) ? returnUrl! : "/" },
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

    // returnUrl-style query parameters feeding into a redirect target are a classic open-redirect
    // vector — the naive implementation ("just redirect to whatever was passed") is also the
    // shortest one to write. Reject anything that isn't a single-slash-prefixed, same-origin
    // relative path: protocol-relative (`//evil.example`) and backslash (`/\evil.example`)
    // variants are real browser-recognized bypasses for a check that only tests StartsWith("/"),
    // and an embedded `://` catches an absolute URL slipped in after a leading slash. Also reject
    // any control character (tab/CR/LF included): the WHATWG URL parser strips ASCII tab and
    // newline from a URL before resolving it, so a value like "/\t/evil.example" — which passes
    // every check above — reaches the browser as a Location header and is then re-parsed as
    // "//evil.example", a protocol-relative redirect off-origin.
    internal static bool IsSafeLocalReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) &&
        returnUrl.StartsWith('/') &&
        !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/\\", StringComparison.Ordinal) &&
        !returnUrl.Contains("://", StringComparison.Ordinal) &&
        !returnUrl.Any(char.IsControl);
}
