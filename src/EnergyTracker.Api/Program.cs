using EnergyTracker.Api;
using EnergyTracker.Api.Composition;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

// Database:Provider is read exactly once, here at the composition root (Consistency Conventions) —
// nothing in Infrastructure re-reads or branches on it independently.
var databaseProvider = builder.Configuration["Database:Provider"] ?? "Postgres";
// Development only (audit S7): matches docker-compose.yml's default POSTGRES_USER/POSTGRES_DB and
// .env.example's default POSTGRES_PASSWORD, so `dotnet run` against `docker compose up postgres -d` works
// with no extra configuration as long as .env's password wasn't changed from the example. Outside
// Development a missing connection string stops the host instead of quietly using these defaults.
const string DevelopmentConnectionString = "Host=localhost;Database=energytracker;Username=energytracker;Password=change-me";
var configuredConnectionString = builder.Configuration.GetConnectionString("Default");
string connectionString;
if (string.IsNullOrWhiteSpace(configuredConnectionString))
{
    // Blank counts as missing: Container Apps and .env express "not configured" as an empty value (D4), and
    // `??` would not catch it. The message names the key and never contains a configured value.
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Default is not set. Set it (environment variable ConnectionStrings__Default) to " +
            "the database connection string; only Development falls back to a local default. See docs/self-hosting.md.");
    }

    connectionString = DevelopmentConnectionString;
}
else
{
    connectionString = configuredConnectionString;
}

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
            //
            // CommandTimeout is left at the 30s ADO.NET default here. It was briefly raised to 120s
            // globally (2026-09-05, to cover Basic-tier Azure SQL hitting 100% DTU during a large
            // SmartPlugReadings import) but that applied to every command app-wide, not just the
            // import path that needed the headroom. spec-db-command-timeout-scope narrows this: the
            // elevated 120s now lives only on the DbContext scope used by the Smart Plug import write
            // path (see SmartPlugImportRepository.AddAsyncCore's SetCommandTimeout call), so an
            // unrelated slow/blocked query elsewhere still fails fast at 30s. UpdateMappingAsync's own
            // separate 180s (elsewhere in the same class) is untouched by and independent of this —
            // 120s does not uniformly cover the whole Smart Plug import lifecycle, only AddAsyncCore's
            // scope.
            options.UseSqlServer(connectionString,
                o => o.MigrationsAssembly("EnergyTracker.Infrastructure.Migrations.SqlServer").MaxBatchSize(1000));
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported Database:Provider '{databaseProvider}'. Expected 'Postgres' or 'SqlServer'.");
    }
}

builder.Services.AddDbContext<EnergyTrackerDbContext>(ConfigureDbContext);
// Backs HouseholdMembershipResolver's own lookup (the accessor only reads its cached result) — see
// HouseholdMembershipDbContext's doc comment for why it's a separate context type rather than reusing
// EnergyTrackerDbContext.
builder.Services.AddDbContextFactory<HouseholdMembershipDbContext>(ConfigureDbContext);

builder.Services.AddHttpContextAccessor();
// Story 11.8: the clock comes from DI so the session cap can be tested; tests may substitute it.
builder.Services.TryAddSingleton(TimeProvider.System);

var oidcConfigured = builder.AddAuth();

builder.Services.AddProblemDetails();
// AD-27 (audit S3): per-route rate-limit policies, applied with RequireRateLimiting on the routes in
// the Story 11.7 rate-limit table. In-process, so correct for one instance (AD-24).
builder.Services.AddHttpRateLimiting();

// AC #4: Data Protection keys persisted externally, not regenerated in memory on cold start —
// the one registration that makes sessions survive a scale-to-zero Azure Container Apps restart.
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .PersistKeysToDbContext<EnergyTrackerDbContext>();

// AD-17 (audit S2): the key ring is protected by exactly one of Azure Key Vault, a certificate or an
// explicit opt-out, chosen from DataProtection:* here, once. Outside Development the app refuses to
// start when none is configured (fail closed). Without a protector ASP.NET Core has no DPAPI to fall
// back on (Linux/Container Apps) and would persist the key XML in plaintext in the same database as
// the household data, so anyone with DB read access or a backup could forge a session cookie.
var dataProtectionKeyProtection = DataProtectionKeyProtection.Configure(
    dataProtectionBuilder, builder.Configuration, builder.Environment);

builder.Services.AddApplicationServices();

// Task 1's decided upload cap (HouseholdImportEndpoints.MaxFileSizeBytes) exceeds FormOptions'
// default MultipartBodyLengthLimit (128 MB) — raised once, here at the composition root.
// FormOptions.MultipartBodyLengthLimit is a secondary, form-parsing-stage limit only reached
// after Kestrel's own MaxRequestBodySize gate (below) has already let the body through, so
// raising it globally is harmless as long as that primary gate stays correctly scoped: the
// Kestrel-level raise is deliberately NOT applied globally here — see the route-scoped middleware
// below (Code Review, Story 7.2 Pass 1: a process-wide Kestrel raise previously widened
// SmartPlugImportEndpoints' own DoS exposure too, since Kestrel would then buffer up to this same
// 250 MB for every route before that endpoint's own lower 20 MB app-level check ever got a chance
// to run).
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = HouseholdImportEndpoints.MaxFileSizeBytes);

// AD-8 (audit C1): the AI backend comes from the Ai:* section only, read once inside this call; see
// AiPlausibilityBackendConfiguration. An invalid Ai:Endpoint stops startup here.
var aiPlausibilityBackend = AiPlausibilityBackendConfiguration.Configure(builder.Services, builder.Configuration);

builder.Services.AddJobQueue(builder.Configuration);

var app = builder.Build();

// Logs the active Data Protection protector and, only when DataProtection:RevokeKeysCreatedBeforeUtc is
// set, runs the one-time key revocation (AD-17 cutover). Must run before the first request.
DataProtectionKeyProtection.ApplyStartupActions(app.Services, dataProtectionKeyProtection, app.Logger);

// States which AI adapter the Ai:* section selected (never the API key), so a self-hoster can see in the log
// whether their configuration took effect.
AiPlausibilityBackendConfiguration.LogSelection(app.Logger, aiPlausibilityBackend);

// AD-27 (audit S3): the security headers middleware is registered FIRST so it also covers error,
// rate-limit and static-file responses. Production only: Vite dev mode injects inline scripts.
if (!app.Environment.IsDevelopment())
{
    app.UseSecurityHeaders();
}

// Must run before anything reads Request.Scheme/Host — Azure Container Apps (this story's own
// deploy target, infra/modules/container-app.bicep) terminates TLS at its ingress and forwards
// plain HTTP to the container. Without this, the OIDC handler builds an http:// redirect_uri
// instead of https://, breaking login against any provider that requires an exact match.
// Container Apps' external ingress is the only path into the container (no direct access
// bypassing it), so trusting X-Forwarded-* headers here without a KnownProxies allowlist is safe.
// Self-host (docs/self-hosting.md) must put a reverse proxy in front of the container: this trust
// extends to any direct caller otherwise. The OIDC redirect_uri and the cookie Secure-flag
// decision read Request.Scheme and are not exploitable by a client lying about its own scheme, but
// the /login rate limiter (AD-27) keys on the client IP resolved here. On Azure that key is not
// spoofable: ingress supplies the rightmost X-Forwarded-For entry and ForwardLimit stays at its
// default of 1 (confirmed by a live probe, Story 11.7). Do not narrow these networks to "fix" that:
// it re-breaks Story 1.7. See Story 1.7 and 11.7 Dev Notes for the per-target analysis.
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

// AD-27: HSTS over HTTPS only. After UseForwardedHeaders, which is what makes Request.IsHttps true
// behind Container Apps' TLS-terminating ingress. The framework skips localhost and plain HTTP.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Scopes the raised body-size limit to just /api/household-import (Code Review, Story 7.2 Pass 1)
// — Minimal API's IFormFile model binding reads the whole body as part of invoking the endpoint,
// before the lambda body itself ever runs, so an in-handler override would be too late; this must
// run as middleware, before routing/endpoint execution, to actually take effect first. Every
// other route keeps Kestrel's original default MaxRequestBodySize (~28.6 MB) — including
// SmartPlugImportEndpoints, whose own lower 20 MB app-level cap this restores the original
// headroom for.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/household-import"))
    {
        var maxRequestBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxRequestBodySizeFeature is { IsReadOnly: false })
        {
            maxRequestBodySizeFeature.MaxRequestBodySize =
                HouseholdImportEndpoints.MaxFileSizeBytes + HouseholdImportEndpoints.BodySizeHeadroomBytes;
        }
    }

    await next(context);
});

// AddProblemDetails() above only backs endpoints that explicitly call Results.Problem(...) —
// without this, unhandled exceptions bypass RFC 7807 entirely and return a bare empty 500.
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();
// AD-27: after routing (implicit in WebApplication) and authentication, so identity-keyed policies see
// the principal; after authorization so unauthenticated requests get a cheap 401 and never create
// identity buckets. /login has no authorization metadata and still reaches the limiter.
app.UseRateLimiter();
// Audit C2 (Story 11.12): after authorization and the rate limiter so a 401 or 429 costs no lookup, after
// UseExceptionHandler so a database failure answers the usual RFC 7807 500, before the endpoints that read
// the cached Household. HouseholdMembershipIsResolvedOncePerRequestTests pins this position.
app.UseHouseholdMembership();

// Liveness only — no DB/dependency check (AD-19): a slow Postgres/Azure SQL must never fail this probe.
// Deliberately outside the /api auth requirement below and unaffected by it (AD-19 regression guard).
app.MapGet("/health", () => Results.Ok());

app.MapAuthEndpoints(oidcConfigured);

// Every route under /api requires an authenticated principal (AC #5). /health and static
// SPA asset serving stay outside this group — the browser must be able to load the app shell
// itself before any login can happen, and /health must never require auth (AD-19).
// Story 11.8 (AD-27, audit S9): every POST/PUT/PATCH/DELETE under /api needs X-Requested-With, via one
// endpoint filter on the group. RequestedWithRoutesTests fails for a mutation route that lacks it.
var api = app.MapGroup("/api").RequireAuthorization().RequireRequestedWith();
api.MapSessionEndpoints();
api.MapHouseholdEndpoints();
api.MapHouseholdInviteEndpoints();
api.MapHouseholdMemberEndpoints();
api.MapTaggingScaffoldEndpoints();
api.MapMeterReadingEndpoints();
api.MapTariffEndpoints();
api.MapMeterRegressionPromptEndpoints();
api.MapStatusEndpoints();
api.MapSmartPlugReadingEndpoints();
api.MapSmartPlugImportEndpoints();
api.MapEventEndpoints();
api.MapJobEndpoints();
api.MapHouseholdExportEndpoints();
api.MapHouseholdImportEndpoints();

// Single-artifact deployment (AD-13): the API serves the built React SPA from wwwroot/.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
