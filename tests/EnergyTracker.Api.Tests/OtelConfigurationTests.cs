using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace EnergyTracker.Api.Tests;

/// <summary>
/// Regression guard for AD-19's OTel extension: Otel:Exporter must never break startup, whether
/// unset (self-host default) or pointed at either supported exporter.
/// </summary>
public class OtelConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NotARealExporter")]
    public async Task App_starts_and_health_returns_200_regardless_of_Otel_Exporter(string? otelExporter)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (otelExporter is not null)
            {
                builder.UseSetting("Otel:Exporter", otelExporter);
            }
        });

        var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Otlp")]
    [InlineData("otlp")]
    [InlineData("OTLP")]
    [InlineData(" Otlp ")]
    public async Task App_starts_with_Otlp_exporter_configured_regardless_of_casing_or_whitespace(string otelExporter)
    {
        // Regression guard: writeToProviders and the exporter switch used to compare otelExporter
        // with different case sensitivity, so "otlp"/"OTLP" silently disabled all telemetry with
        // no error (caught in code review). This asserts the app starts either way; there's no
        // black-box way from this test to assert telemetry was actually registered, but the fix
        // itself (normalizing otelExporter once, up front) makes the two comparisons impossible
        // to disagree again.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Otel:Exporter", otelExporter);
            builder.UseSetting("Otel:OtlpEndpoint", "http://localhost:18889");
        });

        var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uri")]
    [InlineData("aspire-dashboard:18889")]
    public async Task App_starts_with_Otlp_exporter_and_invalid_OtlpEndpoint(string otlpEndpoint)
    {
        // Regression guard: an unguarded `new Uri(otlpEndpoint)` used to throw UriFormatException
        // at startup for a blank endpoint (appsettings.json's default) and mis-parse a
        // scheme-less host:port string as a valid Uri with a bogus scheme instead of failing
        // (caught in code review). Both must now degrade to "OTel off" rather than crash or
        // silently target a wrong endpoint.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Otel:Exporter", "Otlp");
            builder.UseSetting("Otel:OtlpEndpoint", otlpEndpoint);
        });

        var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task App_starts_with_AzureMonitor_exporter_and_blank_connection_string()
    {
        // Regression guard: a blank Otel:AzureMonitorConnectionString used to crash startup with
        // InvalidOperationException ("Connection string starts with separator ';'") — confirmed
        // empirically in code review. Must degrade to "OTel off" like every other invalid-config
        // case, not take the whole app down.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Otel:Exporter", "AzureMonitor");
        });

        var client = factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
