using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;

namespace EnergyTracker.Api.Tests;

// Story 11.22 (AC #3, #4; AD-26, ADR 0008): the exported server span for each invite request carries the marker, not the token.
// The otlp branch runs here; the azuremonitor branch has no test exporter, so a structural guard in Architecture.Tests pins its call.
public class InviteTokenIsNotInTracesTests(EnergyTrackerApiFactory factory) : IClassFixture<EnergyTrackerApiFactory>
{
    private const int ExpectedServerSpans = 4;

    // A server span ends after the response reaches the client, so the test waits on this signal instead of sleeping.
    private sealed class CapturingExporter(ConcurrentQueue<Activity> exported, TaskCompletionSource allArrived) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exported.Enqueue(activity);
            }

            if (exported.Count(a => a.Kind == ActivityKind.Server) >= ExpectedServerSpans)
            {
                allArrived.TrySetResult();
            }

            return ExportResult.Success;
        }
    }

    private async Task<(List<Activity> Server, string Token)> RunAsync()
    {
        var exported = new ConcurrentQueue<Activity>();
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = InviteTokenRequests.NewToken();
        await using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Otel:Exporter", "otlp");
            b.UseSetting("Otel:OtlpEndpoint", "http://localhost:18889");
            b.ConfigureTestServices(s => s.AddOpenTelemetry().WithTracing(t =>
                t.AddProcessor(new SimpleActivityExportProcessor(new CapturingExporter(exported, allArrived)))));
        });
        var client = InviteTokenRequests.CreateClient(host.CreateClient());

        await InviteTokenRequests.FireAsync(client, token);
        await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return ([.. exported.Where(a => a.Kind == ActivityKind.Server)], token);
    }

    private static IEnumerable<string> VisibleText(Activity activity) =>
        activity.TagObjects.Select(t => t.Value?.ToString() ?? string.Empty).Append(activity.DisplayName).Append(activity.OperationName);

    [Fact]
    public async Task No_exported_server_span_carries_the_token()
    {
        var (server, token) = await RunAsync();

        server.Count.ShouldBeGreaterThanOrEqualTo(ExpectedServerSpans);
        var leaks = server.SelectMany(VisibleText).Where(v => v.Contains(token, StringComparison.Ordinal)).ToList();
        leaks.ShouldBeEmpty("an exported span still carries the presented invite token (Story 11.22).");
    }

    [Fact]
    public async Task Spans_show_the_marker_in_url_path_and_query_and_keep_the_route()
    {
        var (server, _) = await RunAsync();

        string Tag(Activity a, string key) => a.GetTagItem(key)?.ToString() ?? string.Empty;
        var paths = server.Select(a => Tag(a, "url.path")).ToList();

        paths.ShouldContain("/join/{token}");
        paths.ShouldContain("/api/household-invites/{token}");
        paths.ShouldContain("/api/household-invites/{token}/accept");
        // OpenTelemetry.Instrumentation.AspNetCore 1.20.1 already redacts returnUrl on its own ("?returnUrl=Redacted");
        // the hook stays as the guarantee that does not depend on that default (ADR 0008).
        server.Where(a => Tag(a, "url.path") == "/login").Select(a => Tag(a, "url.query"))
            .ShouldContain(q => q.StartsWith("?returnUrl=", StringComparison.Ordinal));
        server.Select(a => Tag(a, "http.route")).ShouldContain("/api/household-invites/{token}");
    }
}
