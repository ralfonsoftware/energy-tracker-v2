using EnergyTracker.Api;
using EnergyTracker.Api.Composition;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ADR 0009
StartupFailureExit.Register();

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

// Database:Provider is read exactly once, here at the composition root (Consistency Conventions) —
// nothing in Infrastructure re-reads or branches on it independently.
var databaseProvider = builder.Configuration["Database:Provider"] ?? "Postgres";
// Checked here, not only inside ConfigureDbContext: that callback runs at the first DbContext resolve, where the
// hosted services catch the exception and the host would keep running (ADR 0009).
if (databaseProvider.ToLowerInvariant() is not ("postgres" or "sqlserver"))
{
    throw new InvalidOperationException(
        $"Unsupported Database:Provider '{databaseProvider}'. Expected 'Postgres' or 'SqlServer'.");
}

// Development only: matches docker-compose.yml and .env.example defaults. Outside Development a missing
// connection string stops the host instead of using these.
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
            // MaxBatchSize 1000: the default 42 made a full-history import round-trip in tiny batches (15+ minutes on Basic-tier
            // Azure SQL). EF clamps it to the 2100-parameter limit. CommandTimeout stays 30s; the import path raises its own (ADR 0005).
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
// The clock comes from DI so the session cap can be tested; tests may substitute it.
builder.Services.TryAddSingleton(TimeProvider.System);

var oidcConfigured = builder.AddAuth();

builder.Services.AddProblemDetails();
// AD-27: per-route rate-limit policies, applied with RequireRateLimiting on the routes. In-process, so correct for one instance (AD-24).
builder.Services.AddHttpRateLimiting();

// Data Protection keys persisted externally, not regenerated in memory on cold start: this makes sessions
// survive a scale-to-zero Azure Container Apps restart.
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .PersistKeysToDbContext<EnergyTrackerDbContext>();

// AD-17: the key ring is protected by exactly one of Key Vault, a certificate or an explicit opt-out, chosen
// once. Outside Development none configured stops the host: without a protector the keys would sit in plaintext in the database.
var dataProtectionKeyProtection = DataProtectionKeyProtection.Configure(
    dataProtectionBuilder, builder.Configuration, builder.Environment);

builder.Services.AddApplicationServices();

// Raised once, here: the upload cap exceeds FormOptions' default. The Kestrel limit is raised per route, not globally. ADR 0004.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = HouseholdImportEndpoints.MaxFileSizeBytes);

// AD-8: the AI backend comes from the Ai:* section only, read once inside this call; see
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

// AD-27: the security headers middleware is registered FIRST so it also covers error,
// rate-limit and static-file responses. Production only: Vite dev mode injects inline scripts.
if (!app.Environment.IsDevelopment())
{
    app.UseSecurityHeaders();
}

// Must run before anything reads Request.Scheme/Host (TLS ends at the Container Apps ingress). No KnownProxies allowlist on
// purpose: the ingress is the only way in, and the /login rate-limit key is not spoofable there. Do not narrow it.
// Without a reverse proxy this empty trust extends to any direct caller, so self-hosters must front the app with one. ADR 0003.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// KnownIPNetworks/KnownProxies default to loopback-only, which Container Apps' internal ingress
// peer never matches — without clearing these, the middleware silently ignores the headers
// above and Request.Scheme stays "http".
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// AD-27: HSTS over HTTPS only. After UseForwardedHeaders, which is what makes Request.IsHttps true
// behind Container Apps' TLS-terminating ingress. The framework skips localhost and plain HTTP.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// The raised body limit applies to /api/household-import only, and must run as middleware: IFormFile binding reads the body
// before the handler. ADR 0004.
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
// After authorization and the rate limiter so a 401 or 429 costs no lookup, after
// UseExceptionHandler so a database failure answers the usual RFC 7807 500, before the endpoints that read
// the cached Household. HouseholdMembershipIsResolvedOncePerRequestTests pins this position.
app.UseHouseholdMembership();

// Liveness only — no DB/dependency check (AD-19): a slow Postgres/Azure SQL must never fail this probe.
// Deliberately outside the /api auth requirement below and unaffected by it (AD-19 regression guard).
app.MapGet("/health", () => Results.Ok());

app.MapAuthEndpoints(oidcConfigured);

// Every /api route needs an authenticated principal; /health and the SPA assets stay outside (AD-19). Every mutation needs
// X-Requested-With, via one group filter (AD-27); RequestedWithRoutesTests fails for a route without it.
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
