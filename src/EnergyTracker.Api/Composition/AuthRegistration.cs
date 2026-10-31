using System.Security.Claims;
using EnergyTracker.Application;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;


namespace EnergyTracker.Api.Composition;

internal static class AuthRegistration
{
    public static bool AddAuth(this WebApplicationBuilder builder)
    {
        // Oidc:Authority/Oidc:ClientId/Oidc:ClientSecret are read exactly once, here at the composition
        // root (Consistency Conventions, NFR3/AC #6) — nothing downstream re-reads or branches on them.
        var oidcAuthority = builder.Configuration["Oidc:Authority"] ?? string.Empty;
        var oidcClientId = builder.Configuration["Oidc:ClientId"] ?? string.Empty;
        var oidcClientSecret = builder.Configuration["Oidc:ClientSecret"] ?? string.Empty;

        var authenticationBuilder = builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                // AC #3: server-side httpOnly session cookie — the SPA must never be able to read this
                // (or an equivalent token) via JS.
                options.Cookie.HttpOnly = true;
                // Self-host is expected to sit behind a TLS-terminating reverse proxy or direct HTTPS —
                // never weaken this for convenience.
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // Lax so the cookie survives the OIDC redirect round-trip.
                options.Cookie.SameSite = SameSiteMode.Lax;
                // Matches the actual /login endpoint this story adds (AuthEndpoints.cs) — otherwise this
                // dangles at the framework default (/Account/Login, a route that doesn't exist here) for
                // any future non-/api route that ends up needing RequireAuthorization().
                options.LoginPath = "/login";
                // Story 11.8 (AD-17, audit S13): the 14-day sliding window is written down (it is also the framework
                // default) and a 90-day absolute cap is enforced from the issued-at stamp (SessionLifetime).
                options.ExpireTimeSpan = SessionLifetime.SlidingWindow;
                options.SlidingExpiration = true;
                options.Events.OnSigningIn = context =>
                {
                    SessionLifetime.StampIssuedAt(context);
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = SessionLifetime.ValidateAsync;
                // The cookie handler's default challenge behavior is a 302 to LoginPath — fine for a
                // full-page navigation, but AC #5 requires unauthenticated /api/** calls to 401 so the
                // SPA can detect them via fetch and navigate to /login itself, not follow a redirect.
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        // Register the OIDC scheme only when ClientId and Authority are both set: a half-configured scheme would 500 every route,
        // /health included. Only /login answers 503 until it is configured. ADR 0002.
        var oidcConfigured = !string.IsNullOrEmpty(oidcClientId) && !string.IsNullOrEmpty(oidcAuthority);
        if (oidcConfigured)
        {
            authenticationBuilder.AddOpenIdConnect(options =>
            {
                options.Authority = oidcAuthority;
                options.ClientId = oidcClientId;
                options.ClientSecret = oidcClientSecret;
                options.ResponseType = "code";
                // Auth0 sends no email claim unless the email scope is requested.
                options.Scope.Add("email");
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // Identity lives in the server-side cookie only — never persist provider tokens where
                // anything client-readable could reach them (AC #3).
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.Events = new OpenIdConnectEvents
                {
                    // Keep the validated issuer as its own claim before the userinfo claim merge: issuer + subject is the basis of tenant
                    // isolation (AD-3). ADR 0002.
                    OnTokenValidated = context =>
                    {
                        var validatedIssuer = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Issuer;
                        if (!string.IsNullOrEmpty(validatedIssuer) && context.Principal?.Identity is ClaimsIdentity identity)
                        {
                            identity.AddClaim(new Claim(HouseholdClaimTypes.ValidatedIssuer, validatedIssuer));
                        }

                        return Task.CompletedTask;
                    },
                    // The provider needs client_id on the end-session request, because SaveTokens=false leaves no id_token_hint. ADR 0002.
                    OnRedirectToIdentityProviderForSignOut = context =>
                    {
                        context.ProtocolMessage.ClientId = oidcClientId;
                        // Story 11.8: hand the end-session URL to POST /logout instead of writing a 302.
                        FederatedLogout.Handle(context);
                        return Task.CompletedTask;
                    },
                };
            });
        }

        builder.Services.AddAuthorization();

        return oidcConfigured;
    }
}
