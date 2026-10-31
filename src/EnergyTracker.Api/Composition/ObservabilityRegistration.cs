using System.Globalization;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;


namespace EnergyTracker.Api.Composition;

internal static class ObservabilityRegistration
{
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
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
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture),
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

        return builder;
    }
}
