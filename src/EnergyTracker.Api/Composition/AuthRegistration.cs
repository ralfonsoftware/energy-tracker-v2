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

        // Only register the OIDC scheme when BOTH ClientId and Authority are actually configured (not
        // just ClientId — OpenIdConnectOptions.Validate() also throws on a blank Authority/MetadataAddress,
        // the same failure mode below, just from the other half of this config pair). ASP.NET Core's
        // AuthenticationMiddleware initializes every IAuthenticationRequestHandler scheme on EVERY
        // request (not just OIDC ones) so it can check whether the request matches that scheme's
        // callback path — and a half-configured OIDC scheme would otherwise 500 every route in the app,
        // including /health, the moment this ships without a real provider configured yet (an expected
        // self-host state — Task 6/Dev Notes). Skipping registration entirely keeps the rest of the app
        // functional; only /login (which needs a real provider to mean anything) answers 503 "not configured"
        // until Oidc:Authority/Oidc:ClientId/Oidc:ClientSecret are all set.
        var oidcConfigured = !string.IsNullOrEmpty(oidcClientId) && !string.IsNullOrEmpty(oidcAuthority);
        if (oidcConfigured)
        {
            authenticationBuilder.AddOpenIdConnect(options =>
            {
                options.Authority = oidcAuthority;
                options.ClientId = oidcClientId;
                options.ClientSecret = oidcClientSecret;
                options.ResponseType = "code";
                // Story 8.1/AC #3, found via live Auth0 verification: the handler's default Scope
                // (openid, profile) never included "email" — HouseholdClaimTypes.ResolveEmail's raw-
                // claim-first/ClaimTypes.Email-fallback logic is correct, but Auth0 (per the OIDC spec)
                // omits the email claim entirely from both the ID token and the userinfo response unless
                // this scope is explicitly requested, so /api/session's Email came back null regardless
                // of claim-key handling. Distinct root cause from Story 3.6's ClaimTypes.Name mapping gap.
                options.Scope.Add("email");
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // Identity lives in the server-side cookie only — never persist provider tokens where
                // anything client-readable could reach them (AC #3).
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.Events = new OpenIdConnectEvents
                {
                    // Capture the ID token's validated issuer as an explicit, dedicated claim right
                    // after token validation — before GetClaimsFromUserInfoEndpoint's claim-merge step
                    // runs below, which is not guaranteed to preserve the original validated
                    // Claim.Issuer on every claim. Issuer+subject is the entire basis for this app's
                    // tenant isolation (ICurrentHouseholdAccessor, HouseholdEndpoints), so it must not
                    // depend on that ambient propagation behavior.
                    OnTokenValidated = context =>
                    {
                        var validatedIssuer = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Issuer;
                        if (!string.IsNullOrEmpty(validatedIssuer) && context.Principal?.Identity is ClaimsIdentity identity)
                        {
                            identity.AddClaim(new Claim(HouseholdClaimTypes.ValidatedIssuer, validatedIssuer));
                        }

                        return Task.CompletedTask;
                    },
                    // Auth0 (and other OIDC providers) validate post_logout_redirect_uri against the
                    // calling application's registered Allowed Logout URLs — but need either
                    // id_token_hint or client_id to know WHICH application that is. SaveTokens=false
                    // above means there's never an id_token to offer as id_token_hint, and the handler
                    // doesn't populate ClientId on the end-session request by default, so without this,
                    // sign-out fails provider-side with "post_logout_redirect_uri is not defined as a
                    // valid URL" even when it genuinely is registered.
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
