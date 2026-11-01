using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;

namespace EnergyTracker.Api;

/// <summary>
/// Rewrites the url tags of the ASP.NET Core server span through <see cref="InviteTokenRedactor"/> before any exporter sees it.
/// The enrich hook runs once the instrumentation has set the tags and before the activity ends, on both exporter branches (ADR 0008).
/// </summary>
internal static class InviteTokenSpanRedaction
{
    private static readonly string[] UrlTags = ["url.path", "url.query", "url.full", "http.target", "http.url"];

    public static TracerProviderBuilder AddInviteTokenRedaction(this TracerProviderBuilder builder) =>
        builder.ConfigureServices(services => services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            var previous = options.EnrichWithHttpRequest;
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                previous?.Invoke(activity, request);
                RedactUrlTags(activity);
            };
        }));

    internal static void RedactUrlTags(Activity activity)
    {
        foreach (var tag in UrlTags)
        {
            if (activity.GetTagItem(tag) is string value)
            {
                var redacted = InviteTokenRedactor.Redact(value);
                if (!string.Equals(redacted, value, StringComparison.Ordinal))
                {
                    activity.SetTag(tag, redacted);
                }
            }
        }
    }
}
