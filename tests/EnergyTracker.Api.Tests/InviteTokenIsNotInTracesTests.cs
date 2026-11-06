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
    // The ASP.NET Core instrumentation listens process-wide, so hosts of parallel test classes also feed this exporter;
    // only spans of this run's trace id are counted and kept.
    private sealed class CapturingExporter(ConcurrentQueue<Activity> exported, TaskCompletionSource allArrived, ActivityTraceId traceId) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                if (activity.TraceId == traceId)
                {
                    exported.Enqueue(activity);
                }
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
        var traceId = ActivityTraceId.CreateRandom();
        var token = InviteTokenRequests.NewToken();
        await using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Otel:Exporter", "otlp");
            b.UseSetting("Otel:OtlpEndpoint", "http://localhost:18889");
            b.ConfigureTestServices(s => s.AddOpenTelemetry().WithTracing(t =>
                t.AddProcessor(new SimpleActivityExportProcessor(new CapturingExporter(exported, allArrived, traceId)))));
        });
        var client = InviteTokenRequests.CreateClient(host.CreateClient());
        client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");

        await InviteTokenRequests.FireAsync(client, token);
        try
        {
            await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Fewer than {ExpectedServerSpans} server spans arrived with the sent trace id {traceId}; check that the host adopts the incoming traceparent.", ex);
        }

        return ([.. exported.Where(a => a.Kind == ActivityKind.Server)], token);
    }

    [Fact]
    public void Spans_of_other_traces_are_dropped_and_spans_of_the_own_trace_are_kept()
    {
        var exported = new ConcurrentQueue<Activity>();
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownTraceId = ActivityTraceId.CreateRandom();
        var exporter = new CapturingExporter(exported, allArrived, ownTraceId);

        // Server spans of another host in the same process, as parallel test classes produce them.
        using var source = new ActivitySource("InviteTokenIsNotInTracesTests.foreign");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var foreign = Enumerable.Range(0, ExpectedServerSpans)
            .Select(_ => source.StartActivity("foreign", ActivityKind.Server, parentContext: default)
                ?? throw new InvalidOperationException("The foreign activity was not sampled; the listener is not registered."))
            .ToArray();
        foreach (var activity in foreign)
        {
            activity.Stop();
        }

        exporter.Export(new Batch<Activity>(foreign, foreign.Length));

        allArrived.Task.IsCompleted.ShouldBeFalse("spans of another trace completed the wait.");
        exported.ShouldBeEmpty("spans of another trace entered the list the assertions read.");

        // Accept path: spans of the exporter's own trace are kept and complete the wait.
        var own = Enumerable.Range(0, ExpectedServerSpans)
            .Select(_ => source.StartActivity("own", ActivityKind.Server, new ActivityContext(ownTraceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded))
                ?? throw new InvalidOperationException("The own-trace activity was not sampled; the listener is not registered."))
            .ToArray();
        foreach (var activity in own)
        {
            activity.Stop();
        }

        exporter.Export(new Batch<Activity>(own, own.Length));

        exported.Count.ShouldBe(ExpectedServerSpans);
        allArrived.Task.IsCompleted.ShouldBeTrue("spans of the own trace did not complete the wait.");
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
