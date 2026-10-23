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
        // something the SPA calls via fetch. Without Oidc:Authority/Oidc:ClientId the scheme is
        // never registered (Program.cs), so challenging it would throw: /login is unusable until
        // those are set, and says so with a 503 problem document instead of a 500 (Story 11.11,
        // audit C11). The detail names the settings, never a configured value.
        // returnUrl (Story 1.8) lets the invited person's very first click — /join/{token} —
        // survive the OIDC round trip instead of always landing on "/".
        app.MapGet("/login", IResult (string? returnUrl) =>
            oidcConfigured
                ? Results.Challenge(
                    new AuthenticationProperties { RedirectUri = IsSafeLocalReturnUrl(returnUrl) ? returnUrl! : "/" },
                    [OpenIdConnectDefaults.AuthenticationScheme])
                : Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Sign-in is not configured",
                    detail: "Sign-in is not configured on this server. The operator must set Oidc__Authority, " +
                        "Oidc__ClientId and Oidc__ClientSecret; see docs/self-hosting.md."))
            // AD-27: per client IP, the one unauthenticated throttled route (audit S3). On both branches,
            // so an unconfigured instance cannot be hammered either.
            .RequireRateLimiting(RateLimitPolicies.Login);

        // Story 11.8 (AD-27, audit S9): a POST that needs X-Requested-With, so no other site can log a member
        // out with an <img src="/logout">. Answers 200 JSON { redirectUrl } rather than a 302: a fetch cannot
        // follow the provider's cross-origin redirect, and a form POST would hit the CSP's form-action. The SPA
        // navigates to redirectUrl. Signs out the cookie scheme, plus the OIDC scheme (provider-side end-session,
        // if supported) only when it's actually registered — /logout is reachable by anyone, unauthenticated,
        // at any time, unlike /login, so it must not throw when OIDC is still unconfigured (the expected state
        // before a self-hoster sets up a real provider). It stays outside /api, so it needs the filter explicitly.
        app.MapPost("/logout", async (HttpContext http) =>
        {
            // First, so its Set-Cookie is on the response before anything else touches it. No RedirectUri:
            // the cookie handler must not write a redirect of its own.
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            var redirectUrl = "/";
            if (oidcConfigured)
            {
                // The OIDC handler runs FederatedLogout.Handle (Program.cs), which stores the end-session URL
                // in Items instead of writing a redirect.
                await http.SignOutAsync(
                    OpenIdConnectDefaults.AuthenticationScheme,
                    new AuthenticationProperties { RedirectUri = "/" });
                if (http.Items[FederatedLogout.RedirectUrlKey] is string url)
                {
                    redirectUrl = url;
                }
            }

            return Results.Ok(new LogoutResponse(redirectUrl));
        }).RequireRequestedWith();

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

public record LogoutResponse(string RedirectUrl);
