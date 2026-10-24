using System.Net.Http.Headers;
using System.Security.Claims;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Storage.Queues;
using EnergyTracker.Api;
using EnergyTracker.Api.Endpoints;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Infrastructure;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

// Oidc:Authority/Oidc:ClientId/Oidc:ClientSecret are read exactly once, here at the composition
// root (Consistency Conventions, NFR3/AC #6) — nothing downstream re-reads or branches on them.
var oidcAuthority = builder.Configuration["Oidc:Authority"] ?? string.Empty;
var oidcClientId = builder.Configuration["Oidc:ClientId"] ?? string.Empty;
var oidcClientSecret = builder.Configuration["Oidc:ClientSecret"] ?? string.Empty;

builder.Services.AddHttpContextAccessor();
// Story 11.8: the clock comes from DI so the session cap can be tested; tests may substitute it.
builder.Services.TryAddSingleton(TimeProvider.System);

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

builder.Services.AddScoped<IHouseholdRepository, HouseholdRepository>();
builder.Services.AddScoped<ICurrentHouseholdAccessor, CurrentHouseholdAccessor>();
// Singleton like the factory it depends on; the membership middleware calls it once per /api request.
builder.Services.AddSingleton<HouseholdMembershipResolver>();
builder.Services.AddScoped<CreateHousehold>();
builder.Services.AddScoped<CreateHouseholdInvite>();
builder.Services.AddScoped<AcceptHouseholdInvite>();
builder.Services.AddScoped<RemoveHouseholdMember>();
builder.Services.AddScoped<ListHouseholdMembers>();
builder.Services.AddScoped<ListOpenHouseholdInvites>();
builder.Services.AddScoped<RevokeHouseholdInvite>();
builder.Services.AddScoped<SetYearlyBaseline>();
builder.Services.AddScoped<SetHouseholdLocale>();

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
// Singleton: the per-Household lock dictionary must survive across scoped requests to actually
// serialize concurrent recomputes for the same Household (AD-7).
builder.Services.AddSingleton<IHouseholdRecomputeLock, HouseholdRecomputeLock>();
builder.Services.AddScoped<IStatusRecomputeService, StatusRecomputeService>();
builder.Services.AddScoped<ISmartPlugCoverageSignal, SmartPlugCoverageSignal>();
builder.Services.AddScoped<IAuditCorrectionRecorder, AuditCorrectionRecorder>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<CreateMeterReading>();
builder.Services.AddScoped<ResolveMeterRegressionPrompt>();
builder.Services.AddScoped<GetOpenMeterRegressionPrompt>();
builder.Services.AddScoped<GetCurrentStatus>();
builder.Services.AddScoped<GetMeterReadingHistory>();
builder.Services.AddScoped<EditMeterReading>();
builder.Services.AddScoped<IStatusSnapshotRepository, StatusSnapshotRepository>();
builder.Services.AddScoped<GetStatusHistory>();
builder.Services.AddScoped<ISmartPlugReadingRepository, SmartPlugReadingRepository>();
builder.Services.AddScoped<GetPerPlugMeasuredData>();

builder.Services.AddScoped<ITariffRepository, TariffRepository>();
builder.Services.AddScoped<CreateTariff>();
builder.Services.AddScoped<GetTariffHistory>();
builder.Services.AddScoped<EditTariff>();
builder.Services.AddScoped<CompareTariff>();
builder.Services.AddScoped<GetTariffCheckReminder>();

builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<CreateEvent>();
builder.Services.AddScoped<GetEventHistory>();
builder.Services.AddScoped<SetAiPlausibilityEnabled>();
builder.Services.AddScoped<CorrelateEvent>();
builder.Services.AddScoped<RequeueEventCorrelations>();

builder.Services.AddScoped<IHouseholdExportReader, HouseholdExportReader>();
builder.Services.AddScoped<ExportHouseholdData>();

// Story 7.2 — restore/migration import. Registry is a singleton (in-memory token→temp-file
// mapping, MemoryHouseholdImportUploadRegistry's own doc comment); the writer/use case are scoped
// like every other DbContext-backed adapter/use case in this file.
builder.Services.AddSingleton<IHouseholdImportUploadRegistry, MemoryHouseholdImportUploadRegistry>();
builder.Services.AddSingleton(new HouseholdImportUploadSettings(Path.GetTempPath(), HouseholdImportEndpoints.MaxFileSizeBytes));
builder.Services.AddScoped<ValidateHouseholdImport>();
builder.Services.AddScoped<IHouseholdRestoreWriter, HouseholdRestoreWriter>();
builder.Services.AddScoped<RestoreHouseholdData>();

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

// AD-6: JobQueue:Provider is read exactly once, here at the composition root — same
// switch-on-lowercased-config-value shape as Database:Provider/Otel:Exporter above.
builder.Services.AddScoped<JobHouseholdContext>();
builder.Services.AddSingleton<BackgroundJobProcessor>();
// Singleton — both queue adapters below (InProcessChannelJobQueue/AzureStorageQueueJobQueue) are
// themselves singletons and inject this directly (Story 3.6/AD-6 extension).
builder.Services.AddSingleton<BackgroundJobEnqueueRecorder>();
builder.Services.AddScoped<IBackgroundJobRepository, BackgroundJobRepository>();
// AD-6 (amended 2026-10-10): the one writer of BackgroundJob.Status. Timings are operational
// constants (1 min heartbeat, 5 min stale), not Household configuration.
builder.Services.AddSingleton(JobLifecycleTimings.Default);
builder.Services.AddScoped<IBackgroundJobLifecycle, BackgroundJobLifecycle>();
builder.Services.AddScoped<GetBackgroundJobStatus>();
builder.Services.AddScoped<ListSmartPlugImportJobs>();
builder.Services.AddScoped<CleanUpSmartPlugImportJobs>();
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
        builder.Services.AddSingleton(sp =>
        {
            var queueOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
            // A body that fails Base64 decoding would otherwise make ReceiveMessagesAsync throw forever (audit C7).
            var decodingLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<AzureStorageQueueJobProcessingService>();
            queueOptions.MessageDecodingFailed += args => QueueMessageDecodingFailureHandler.HandleAsync(args, decodingLogger);
            return new QueueClient(jobQueueConnectionString, "jobs", queueOptions);
        });
        builder.Services.AddSingleton(QueueConsumerTimings.Default);
        builder.Services.AddSingleton<IBackgroundJobQueue, AzureStorageQueueJobQueue>();
        builder.Services.AddHostedService<AzureStorageQueueJobProcessingService>();
        break;
    default:
        // Unset/unrecognized: same "unset stays off"-shaped default as Otel:Exporter, not
        // Database:Provider's hard-required default — the in-process adapter needs no external
        // config to function.
        builder.Services.AddSingleton<InProcessChannelJobQueue>();
        // AD-24: process-local startup recovery; the Azure adapter never runs it.
        builder.Services.AddSingleton<InProcessJobStartupSweep>();
        builder.Services.AddSingleton<IBackgroundJobQueue>(sp => sp.GetRequiredService<InProcessChannelJobQueue>());
        builder.Services.AddHostedService<InProcessChannelJobProcessingService>();
        break;
}

var app = builder.Build();

// Logs the active Data Protection protector and, only when DataProtection:RevokeKeysCreatedBeforeUtc is
// set, runs the one-time key revocation (AD-17 cutover). Must run before the first request.
DataProtectionKeyProtection.ApplyStartupActions(app.Services, dataProtectionKeyProtection, app.Logger);

// States which AI adapter the Ai:* section selected (never the API key), so a self-hoster can see in the log
// whether their configuration took effect.
app.Logger.LogInformation("AI plausibility backend: {Backend}", aiPlausibilityBackend.Description);
if (aiPlausibilityBackend.Warning is { } aiPlausibilityWarning)
{
    app.Logger.LogWarning("AI plausibility backend: {Warning}", aiPlausibilityWarning);
}

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
