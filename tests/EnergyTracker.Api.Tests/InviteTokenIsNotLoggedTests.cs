using EnergyTracker.Api;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.22 (AC #2, #4; AD-26, audit S10, ADR 0008): the real host's request lines keep status and timing
// but never carry the presented invite token.
[Collection(SerialLoggingGroup.Name)]
public class InviteTokenIsNotLoggedTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private const string HostingCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    private async Task<(CapturingLogSink Sink, string Token)> RunAsync()
    {
        var sink = new CapturingLogSink();
        var token = InviteTokenRequests.NewToken();
        await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink)));
        var client = InviteTokenRequests.CreateClient(host.CreateClient());

        await InviteTokenRequests.FireAsync(client, token);
        return (sink, token);
    }

    [Fact]
    public async Task No_log_line_or_property_carries_the_token()
    {
        var (sink, token) = await RunAsync();

        sink.Events.ShouldNotBeEmpty();
        var leaks = sink.Events
            .SelectMany(CapturingLogSink.VisibleText)
            .Where(text => text.Contains(token, StringComparison.Ordinal))
            .ToList();
        leaks.ShouldBeEmpty("a log event still carries the presented invite token (Story 11.22).");
    }

    [Fact]
    public async Task Request_lines_still_exist_and_show_the_marker_not_the_token()
    {
        var (sink, _) = await RunAsync();

        var hosting = sink.Events
            .Where(e => e.Properties.TryGetValue("SourceContext", out var c) && c.ToString().Trim('"') == HostingCategory)
            .Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture).Replace("\"", string.Empty, StringComparison.Ordinal))
            .ToList();

        foreach (var expected in InviteTokenRequests.RedactedPathsAndQueries)
        {
            hosting.ShouldContain(m => m.Contains("Request starting", StringComparison.Ordinal) && m.Contains(expected, StringComparison.Ordinal),
                $"no 'Request starting' line with {expected}");
            // The SPA fallback rewrites /join/{token} to /index.html before the finish line, so that line never shows the join path.
            if (expected != "/join/{token}")
            {
                hosting.ShouldContain(m => m.Contains("Request finished", StringComparison.Ordinal) && m.Contains(expected, StringComparison.Ordinal),
                    $"no 'Request finished' line with {expected}");
            }
        }
    }

    [Fact]
    public async Task Request_lines_keep_method_status_and_elapsed_time()
    {
        var (sink, _) = await RunAsync();

        var finished = sink.Events
            .Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture))
            .Where(m => m.Contains("Request finished", StringComparison.Ordinal) && m.Contains("/api/household-invites/{token}", StringComparison.Ordinal))
            .ToList();

        finished.ShouldNotBeEmpty();
        finished.ShouldAllBe(m => m.Contains("GET", StringComparison.Ordinal) || m.Contains("POST", StringComparison.Ordinal));
        finished.ShouldAllBe(m => m.Contains("404", StringComparison.Ordinal) && m.Contains("ms", StringComparison.Ordinal));
    }

    [Fact]
    public void Known_bad_sample_a_raw_request_line_would_fail_the_leak_check()
    {
        var token = InviteTokenRequests.NewToken();
        var rawLine = $"Request starting HTTP/1.1 GET http://localhost/join/{token}";

        rawLine.Contains(token, StringComparison.Ordinal).ShouldBeTrue();
        InviteTokenRedactor.Redact(rawLine).Contains(token, StringComparison.Ordinal).ShouldBeFalse();
    }
}
