using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace EnergyTracker.Api.Tests;

public sealed class CapturingLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => [.. _events];

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    // Everything a log reader could see: the rendered message plus every property value, nested ones included.
    public static IEnumerable<string> VisibleText(LogEvent logEvent)
    {
        yield return logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var property in logEvent.Properties)
        {
            yield return property.Value.ToString();
        }
    }
}
