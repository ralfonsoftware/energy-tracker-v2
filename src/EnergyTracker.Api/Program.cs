using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Storage.Queues;
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
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// WebApplicationBuilder registers a default Console/Debug/EventSource/EventLog provider set that
// UseSerilog's writeToProviders:false (its default) leaves inert but registered — replacing
// ILoggerFactory means nothing ever routes events to them. The Otlp path below sets
// writeToProviders:true so Serilog also reaches the OTel logging provider, but that flag forwards
// to EVERY registered ILoggerProvider indiscriminately; without clearing these defaults first, it
// would also wake the dormant default Console provider and double every console log line
// (confirmed by a docker-compose smoke test during AD-19 OTel extension work). Clearing here,
// unconditionally, keeps Serilog's WriteTo.Console() as the sole console sink on every path.
builder.Logging.ClearProviders();

// AD-19 OTel extension — Otel:Exporter is read exactly once, here at the composition root
// (Consistency Conventions), same as Database:Provider/Oidc:* below. Read before UseSerilog:
// its writeToProviders argument depends on this value. Normalized to lower-invariant immediately
// (mirrors databaseProvider.ToLowerInvariant() below) so every comparison against it — including
// the writeToProviders check right below — agrees on case; a prior version compared writeToProviders
// case-insensitively but switched on the raw value case-sensitively, so "otlp"/"OTLP" silently
// disabled all telemetry with no error (caught in code review, AD-19 OTel extension work).
var otelExporter = (builder.Configuration["Otel:Exporter"] ?? string.Empty).Trim().ToLowerInvariant();

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(),
    // Otlp path forwards Serilog's events into OTel's log pipeline too (Aspire Dashboard,
    // trace-correlated). AzureMonitor path must NOT do this: Application Insights is
    // workspace-based on the same Log Analytics workspace Container Apps already streams stdout
    // into, so forwarding Serilog through OTel there as well would double-ingest every log line
    // against the shared dailyQuotaGb cap (ARCHITECTURE-SPINE.md AD-19 extension). This same flag
    // also governs whether Azure Monitor's own bundled logging provider (registered below via
    // UseAzureMonitor) ever receives events — Serilog owns ILoggerFactory outright once
    // UseSerilog runs, so nothing else in the provider list gets called unless this is true.
    writeToProviders: otelExporter == "otlp");

switch (otelExporter)
{
    case "azuremonitor":
        var azureMonitorConnectionString = (builder.Configuration["Otel:AzureMonitorConnectionString"] ?? string.Empty).Trim();
        // A blank/missing connection string is not a "do nothing" no-op here — UseAzureMonitor
        // throws InvalidOperationException at startup ("Connection string starts with separator
        // ';'"), confirmed empirically in code review. Guard it the same way the Otlp branch
        // below guards a blank/invalid endpoint: skip registration entirely rather than crash.
        if (!string.IsNullOrEmpty(azureMonitorConnectionString))
        {
            // Traces + metrics only (see writeToProviders comment above for why logs stay off
            // this path). UseAzureMonitor already bundles ASP.NET Core/HttpClient instrumentation;
            // EF Core and runtime metrics are added explicitly since the Distro doesn't cover
            // them, and this app runs against both Postgres and SQL Server depending on
            // Database:Provider.
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService("EnergyTracker.Api"))
                .UseAzureMonitor(o => o.ConnectionString = azureMonitorConnectionString)
                .WithTracing(t => t.AddEntityFrameworkCoreInstrumentation())
                .WithMetrics(m => m.AddRuntimeInstrumentation());
        }
        break;

    case "otlp":
        var otlpEndpoint = (builder.Configuration["Otel:OtlpEndpoint"] ?? string.Empty).Trim();
        // A blank/malformed/scheme-less endpoint (e.g. new Uri("aspire-dashboard:18889") parses
        // "aspire-dashboard" as the scheme instead of throwing) must not crash the app — confirmed
        // empirically that an unguarded `new Uri(...)` here throws UriFormatException on the
        // appsettings.json default (blank). Require an absolute http/https URI; anything else
        // skips OTel registration entirely, same graceful-degrade shape as the AzureMonitor guard
        // above and the unset/unrecognized default case below.
        if (Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var otlpEndpointUri) &&
            (otlpEndpointUri.Scheme == Uri.UriSchemeHttp || otlpEndpointUri.Scheme == Uri.UriSchemeHttps))
        {
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService("EnergyTracker.Api"))
                .WithTracing(t => t
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    .AddOtlpExporter(o => o.Endpoint = otlpEndpointUri))
                .WithMetrics(m => m
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddOtlpExporter(o => o.Endpoint = otlpEndpointUri));
            builder.Logging.AddOpenTelemetry(o => o.AddOtlpExporter(exporter => exporter.Endpoint = otlpEndpointUri));
        }
        break;

    default:
        // Unset/unrecognized: OTel stays fully off — same graceful-degrade shape as unconfigured
        // OIDC below. Must not throw or affect any other route.
        break;
}

// Database:Provider is read exactly once, here at the composition root (Consistency Conventions) —
// nothing in Infrastructure re-reads or branches on it independently.
var databaseProvider = builder.Configuration["Database:Provider"] ?? "Postgres";
// Matches docker-compose.yml's default POSTGRES_USER/POSTGRES_DB and .env.example's default
// POSTGRES_PASSWORD, so `dotnet run` against `docker compose up postgres -d` works with no
// extra configuration as long as .env's password wasn't changed from the example.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Database=energytracker;Username=energytracker;Password=change-me";

void ConfigureDbContext(DbContextOptionsBuilder options)
{
    switch (databaseProvider.ToLowerInvariant())
    {
        case "postgres":
            options.UseNpgsql(connectionString,
                o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.Postgres").MaxBatchSize(1000));
            break;
        case "sqlserver":
            // Default MaxBatchSize (42) meant a large Smart Plug import (Story 3.3 — a full-history
            // Eve Home export can be hundreds of thousands of SmartPlugReading rows) round-tripped
            // to the DB in tiny batches; on Basic-tier Azure SQL each round trip's latency dominated,
            // stretching one import to 15+ minutes and starving every job queued behind it (AD-6 has
            // exactly one worker). EF clamps this to whatever fits SQL Server's 2100-parameter
            // batch limit for the widest entity being saved, so 1000 is a safe upper bound, not a
            // literal row count.
            options.UseSqlServer(connectionString,
                o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer").MaxBatchSize(1000));
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported Database:Provider '{databaseProvider}'. Expected 'Postgres' or 'SqlServer'.");
    }
}

builder.Services.AddDbContext<EnergyTrackerDbContext>(ConfigureDbContext);
// Backs CurrentHouseholdAccessor's own lookup — see HouseholdMembershipDbContext's doc comment
// for why it's a separate context type rather than reusing EnergyTrackerDbContext.
builder.Services.AddDbContextFactory<HouseholdMembershipDbContext>(ConfigureDbContext);

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
builder.Services.AddScoped<CreateHouseholdInvite>();
builder.Services.AddScoped<AcceptHouseholdInvite>();
builder.Services.AddScoped<SetYearlyBaseline>();

builder.Services.AddScoped<ITaggingScaffoldRepository, TaggingScaffoldRepository>();
builder.Services.AddScoped<CreateRoom>();
builder.Services.AddScoped<RenameRoom>();
builder.Services.AddScoped<ArchiveRoom>();
builder.Services.AddScoped<CreatePowerPoint>();
builder.Services.AddScoped<RenamePowerPoint>();
builder.Services.AddScoped<ArchivePowerPoint>();
builder.Services.AddScoped<MovePowerPoint>();
builder.Services.AddScoped<CreateDevice>();
builder.Services.AddScoped<RenameDevice>();
builder.Services.AddScoped<ArchiveDevice>();
builder.Services.AddScoped<MoveDevice>();

builder.Services.AddScoped<IMeterReadingRepository, MeterReadingRepository>();
builder.Services.AddScoped<IMeterRegressionPromptRepository, MeterRegressionPromptRepository>();
builder.Services.AddScoped<IStatusRecomputeService, StatusRecomputeService>();
builder.Services.AddScoped<ISmartPlugCoverageSignal, SmartPlugCoverageSignal>();
builder.Services.AddScoped<CreateMeterReading>();
builder.Services.AddScoped<ResolveMeterRegressionPrompt>();
builder.Services.AddScoped<GetOpenMeterRegressionPrompt>();
builder.Services.AddScoped<GetCurrentStatus>();

// AD-6: JobQueue:Provider is read exactly once, here at the composition root — same
// switch-on-lowercased-config-value shape as Database:Provider/Otel:Exporter above.
builder.Services.AddScoped<JobHouseholdContext>();
builder.Services.AddSingleton<BackgroundJobProcessor>();
builder.Services.AddScoped<IBackgroundJobRepository, BackgroundJobRepository>();
builder.Services.AddScoped<GetBackgroundJobStatus>();
builder.Services.AddScoped<ISmartPlugParser, EveHomeXlsxParser>();
builder.Services.AddScoped<ISmartPlugParser, MerossCsvParser>();
builder.Services.AddScoped<ISmartPlugImportRepository, SmartPlugImportRepository>();
builder.Services.AddScoped<CompleteSmartPlugImportProcessing>();
builder.Services.AddScoped<ProcessSmartPlugImport>();
builder.Services.AddScoped<MapSmartPlugImportToPowerPoint>();

var jobQueueProvider = (builder.Configuration["JobQueue:Provider"] ?? string.Empty).Trim().ToLowerInvariant();
switch (jobQueueProvider)
{
    case "azurestoragequeue":
        var jobQueueConnectionString = builder.Configuration["JobQueue:ConnectionString"] ?? string.Empty;
        // Base64 message encoding so a JSON payload survives the queue message's XML envelope
        // untouched (the SDK's raw/"None" default is not XML-safe for arbitrary JSON text).
        builder.Services.AddSingleton(_ => new QueueClient(
            jobQueueConnectionString, "jobs", new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }));
        builder.Services.AddSingleton<IBackgroundJobQueue, AzureStorageQueueJobQueue>();
        builder.Services.AddHostedService<AzureStorageQueueJobProcessingService>();
        break;
    default:
        // Unset/unrecognized: same "unset stays off"-shaped default as Otel:Exporter, not
        // Database:Provider's hard-required default — the in-process adapter needs no external
        // config to function.
        builder.Services.AddSingleton<InProcessChannelJobQueue>();
        builder.Services.AddSingleton<IBackgroundJobQueue>(sp => sp.GetRequiredService<InProcessChannelJobQueue>());
        builder.Services.AddHostedService<InProcessChannelJobProcessingService>();
        break;
}

var app = builder.Build();

// Must run before anything reads Request.Scheme/Host — Azure Container Apps (this story's own
// deploy target, infra/modules/container-app.bicep) terminates TLS at its ingress and forwards
// plain HTTP to the container. Without this, the OIDC handler builds an http:// redirect_uri
// instead of https://, breaking login against any provider that requires an exact match.
// Container Apps' external ingress is the only path into the container (no direct access
// bypassing it), so trusting X-Forwarded-* headers here without a KnownProxies allowlist is safe.
// Self-host (docs/self-hosting.md) exposes the container's port directly with no reverse proxy
// required or documented, so this trust extends to any direct caller there too — but nothing in
// this codebase treats RemoteIpAddress or X-Forwarded-For as a security signal (no rate limiting,
// no IP-based auth), and the only consumers of Request.Scheme are the OIDC redirect_uri and the
// cookie Secure-flag decision, neither exploitable by a client lying about its own scheme. See
// Story 1.7 Dev Notes for the full per-target analysis.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// KnownIPNetworks/KnownProxies default to loopback-only, which Container Apps' internal ingress
// peer never matches — without clearing these, the middleware silently ignores the headers
// above and Request.Scheme stays "http" (Story 1.7's production bug).
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

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
api.MapHouseholdInviteEndpoints();
api.MapTaggingScaffoldEndpoints();
api.MapMeterReadingEndpoints();
api.MapMeterRegressionPromptEndpoints();
api.MapStatusEndpoints();
api.MapSmartPlugImportEndpoints();
api.MapJobEndpoints();

// Single-artifact deployment (AD-13): the API serves the built React SPA from wwwroot/.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
