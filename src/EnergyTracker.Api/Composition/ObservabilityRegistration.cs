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
        // Clear the default logger providers first: with writeToProviders on they would double every console line. ADR 0001.
        builder.Logging.ClearProviders();

        // Otel:Exporter is read once and normalized here (AD-19); every comparison below, writeToProviders included, uses this value.
        var otelExporter = (builder.Configuration["Otel:Exporter"] ?? string.Empty).Trim().ToLowerInvariant();

        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture),
            // Only the Otlp path forwards Serilog into OTel's log pipeline; Azure Monitor would double-ingest every line. ADR 0001.
            writeToProviders: otelExporter == "otlp");

        switch (otelExporter)
        {
            case "azuremonitor":
                var azureMonitorConnectionString = (builder.Configuration["Otel:AzureMonitorConnectionString"] ?? string.Empty).Trim();
                // A blank connection string makes UseAzureMonitor throw at startup, so skip the registration instead (ADR 0001).
                if (!string.IsNullOrEmpty(azureMonitorConnectionString))
                {
                    // Traces and metrics only; EF Core and runtime instrumentation are added because the distro does not cover them.
                    builder.Services.AddOpenTelemetry()
                        .ConfigureResource(r => r.AddService("EnergyTracker.Api"))
                        .UseAzureMonitor(o => o.ConnectionString = azureMonitorConnectionString)
                        .WithTracing(t => t.AddEntityFrameworkCoreInstrumentation())
                        .WithMetrics(m => m.AddRuntimeInstrumentation());
                }
                break;

            case "otlp":
                var otlpEndpoint = (builder.Configuration["Otel:OtlpEndpoint"] ?? string.Empty).Trim();
                // Only an absolute http/https endpoint counts; anything else degrades to OTel off instead of crashing (ADR 0001).
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
