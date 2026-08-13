using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// Database:Provider is read exactly once, here at the composition root (Consistency Conventions) —
// nothing in Infrastructure re-reads or branches on it independently.
var databaseProvider = builder.Configuration["Database:Provider"] ?? "Postgres";
// Matches docker-compose.yml's default POSTGRES_USER/POSTGRES_DB and .env.example's default
// POSTGRES_PASSWORD, so `dotnet run` against `docker compose up postgres -d` works with no
// extra configuration as long as .env's password wasn't changed from the example.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Database=energytracker;Username=energytracker;Password=change-me";

builder.Services.AddDbContext<EnergyTrackerDbContext>(options =>
{
    switch (databaseProvider.ToLowerInvariant())
    {
        case "postgres":
            options.UseNpgsql(connectionString,
                o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres"));
            break;
        case "sqlserver":
            options.UseSqlServer(connectionString,
                o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer"));
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported Database:Provider '{databaseProvider}'. Expected 'Postgres' or 'SqlServer'.");
    }
});

// Oidc:Authority/Oidc:ClientId/Oidc:ClientSecret are read exactly once, here at the composition
// root (Consistency Conventions, NFR3/AC #6) — nothing downstream re-reads or branches on them.
var oidcAuthority = builder.Configuration["Oidc:Authority"] ?? string.Empty;
var oidcClientId = builder.Configuration["Oidc:ClientId"] ?? string.Empty;
var oidcClientSecret = builder.Configuration["Oidc:ClientSecret"] ?? string.Empty;

builder.Services.AddHttpContextAccessor();

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
// functional; only /login (which needs a real provider to mean anything) fails until
// Oidc:Authority/Oidc:ClientId/Oidc:ClientSecret are all set.
var oidcConfigured = !string.IsNullOrEmpty(oidcClientId) && !string.IsNullOrEmpty(oidcAuthority);
if (oidcConfigured)
{
    authenticationBuilder.AddOpenIdConnect(options =>
    {
        options.Authority = oidcAuthority;
        options.ClientId = oidcClientId;
        options.ClientSecret = oidcClientSecret;
        options.ResponseType = "code";
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
        };
    });
}

builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();

// AC #4: Data Protection keys persisted externally, not regenerated in memory on cold start —
// the one registration that makes sessions survive a scale-to-zero Azure Container Apps restart.
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .PersistKeysToDbContext<EnergyTrackerDbContext>();

// Without an encryptor, ASP.NET Core has no DPAPI to fall back on (Linux/Container Apps) and
// persists key XML in plaintext in the same DataProtectionKeys table as Households/
// HouseholdMembers — anyone with DB read access or a backup could forge a valid session cookie
// for any household member. Encrypt with a PKCS12 certificate when one is configured (base64,
// same env-var-secret pattern as Oidc:ClientSecret — no volume mount needed); the cert itself
// is an external dependency this patch cannot self-provision (same category as the OIDC
// provider/GitHub secret gaps already flagged), so this degrades to the previous
// documented-risk behavior when unset rather than failing startup.
var dataProtectionCertificateBase64 = builder.Configuration["DataProtection:CertificateBase64"] ?? string.Empty;
var dataProtectionCertificatePassword = builder.Configuration["DataProtection:CertificatePassword"] ?? string.Empty;
if (!string.IsNullOrEmpty(dataProtectionCertificateBase64))
{
    var certificateBytes = Convert.FromBase64String(dataProtectionCertificateBase64);
    dataProtectionBuilder.ProtectKeysWithCertificate(
        X509CertificateLoader.LoadPkcs12(certificateBytes, dataProtectionCertificatePassword));
}

builder.Services.AddScoped<IHouseholdRepository, HouseholdRepository>();
builder.Services.AddScoped<ICurrentHouseholdAccessor, CurrentHouseholdAccessor>();
builder.Services.AddScoped<CreateHousehold>();

var app = builder.Build();

// Must run before anything reads Request.Scheme/Host — Azure Container Apps (this story's own
// deploy target, infra/modules/container-app.bicep) terminates TLS at its ingress and forwards
// plain HTTP to the container. Without this, the OIDC handler builds an http:// redirect_uri
// instead of https://, breaking login against any provider that requires an exact match.
// Container Apps' external ingress is the only path into the container (no direct access
// bypassing it), so trusting X-Forwarded-* headers here without a KnownProxies allowlist is safe.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

// AddProblemDetails() above only backs endpoints that explicitly call Results.Problem(...) —
// without this, unhandled exceptions bypass RFC 7807 entirely and return a bare empty 500.
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

// Liveness only — no DB/dependency check (AD-19): a slow Postgres/Azure SQL must never fail this probe.
// Deliberately outside the /api auth requirement below and unaffected by it (AD-19 regression guard).
app.MapGet("/health", () => Results.Ok());

app.MapAuthEndpoints(oidcConfigured);

// Every route under /api requires an authenticated principal (AC #5). /health and static
// SPA asset serving stay outside this group — the browser must be able to load the app shell
// itself before any login can happen, and /health must never require auth (AD-19).
var api = app.MapGroup("/api").RequireAuthorization();
api.MapSessionEndpoints();
api.MapHouseholdEndpoints();

// Single-artifact deployment (AD-13): the API serves the built React SPA from wwwroot/.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
