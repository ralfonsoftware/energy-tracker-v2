using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.22 (AC #8; ADR 0001, ADR 0008): the levels written in appsettings*.json are the levels in effect,
// because ReadFrom.Configuration reads only a Serilog section.
[Collection(SerialLoggingGroup.Name)]
public class SerilogConfigurationTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private static string ApiProjectFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnergyTracker.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("EnergyTracker.sln not found above the test binaries.");
        return Path.Combine(dir.FullName, "src", "EnergyTracker.Api", name);
    }

    private async Task<IReadOnlyList<LogEvent>> EmitAsync(string environment)
    {
        var sink = new CapturingLogSink();
        await using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("DataProtection:AllowUnprotectedKeys", "true");
            b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink));
        });
        _ = host.Server; // build the host

        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation("hosting-info");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware").LogInformation("routing-info");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware").LogWarning("routing-warning");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationHandler").LogInformation("auth-info");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager").LogInformation("dataprotection-info");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel").LogInformation("kestrel-info");
        loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogInformation("ef-info");
        loggerFactory.CreateLogger("EnergyTracker.Something").LogDebug("app-debug");
        loggerFactory.CreateLogger("EnergyTracker.Something").LogInformation("app-info");
        return sink.Events;
    }

    private static bool Has(IReadOnlyList<LogEvent> events, string message) =>
        events.Any(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) == message);

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Effective_levels_match_the_Serilog_section(string environment)
    {
        var events = await EmitAsync(environment);

        var seen = string.Join(" | ", events.Select(e => $"{e.Level}:{e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)}"));
        Has(events, "hosting-info").ShouldBeTrue($"request lines must stay at Information (AC #2/#5). Seen: {seen}");
        Has(events, "routing-info").ShouldBeFalse("Microsoft.AspNetCore.* other than Hosting.Diagnostics is Warning");
        Has(events, "routing-warning").ShouldBeTrue();
        Has(events, "auth-info").ShouldBeTrue("Authentication keeps Information (review decision 2026-11-01)");
        Has(events, "dataprotection-info").ShouldBeTrue("DataProtection key-ring lines keep Information (review decision 2026-11-01)");
        Has(events, "kestrel-info").ShouldBeFalse("Kestrel and other Microsoft.AspNetCore.* categories are Warning");
        Has(events, "ef-info").ShouldBeTrue("EF Core is not overridden in this story");
        Has(events, "app-info").ShouldBeTrue();
        Has(events, "app-debug").ShouldBeFalse();
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void Settings_files_use_the_Serilog_schema_and_no_Logging_section(string file)
    {
        var errors = SettingsFileProblems(File.ReadAllText(ApiProjectFile(file)));

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Known_bad_sample_the_old_Logging_only_JSON_fails_the_file_guard()
    {
        const string old = """{ "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } } }""";

        SettingsFileProblems(old).Count.ShouldBe(2);
    }

    private static List<string> SettingsFileProblems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var problems = new List<string>();
        if (doc.RootElement.TryGetProperty("Logging", out _))
        {
            problems.Add("has a top-level Logging key (ignored by Serilog)");
        }

        if (!(doc.RootElement.TryGetProperty("Serilog", out var serilog) && serilog.TryGetProperty("MinimumLevel", out _)))
        {
            problems.Add("has no Serilog:MinimumLevel");
        }

        return problems;
    }
}
