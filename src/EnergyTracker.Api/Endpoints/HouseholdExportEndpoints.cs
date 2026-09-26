using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdExportEndpoints
{
    // ILogger<T> needs a non-static category type — HouseholdExportEndpoints itself is static, so
    // this marker class stands in for it (same reason StatusRecomputeService-style adapters use
    // their own class as T; a static host class can't do that).
    private sealed class LogCategory;

    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Web defaults (camelCase) — matches every other endpoint's implicit Results.Ok() serialization.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    // Same shape as MeterReadingEndpoints.TryGetHouseholdId — copied rather than referenced across
    // files (that helper is private to its own class).
    private static bool TryGetHouseholdId(ICurrentHouseholdAccessor householdAccessor, out Guid householdId, out IResult? forbidden)
    {
        if (householdAccessor.HouseholdId is { } id)
        {
            householdId = id;
            forbidden = null;
            return true;
        }

        householdId = default;
        forbidden = Results.Problem(detail: NoHouseholdDetail, statusCode: StatusCodes.Status403Forbidden);
        return false;
    }

    public static RouteGroupBuilder MapHouseholdExportEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/household-export", async (
            HttpContext context,
            ICurrentHouseholdAccessor householdAccessor,
            ExportHouseholdData exportHouseholdData,
            ILogger<LogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            // spec-household-export-observability.md (Epic 7 retro action item #1): spans the
            // whole request, not just the write — a slow reader-side query is as much a "danger
            // zone" signal as a slow write.
            var stopwatch = Stopwatch.StartNew();
            var export = await exportHouseholdData.ExecuteAsync(householdId, cancellationToken);

            // Derived from the export's own ExportedAtUtc, not a fresh DateTimeOffset.UtcNow call —
            // keeps the downloaded filename's date and the document's own exportedAtUtc field from
            // disagreeing if the request happens to straddle a UTC-midnight boundary.
            var fileName = $"energy-tracker-export-{export.ExportedAtUtc:yyyy-MM-dd}.json";
            context.Response.ContentType = "application/json";
            context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
            // No Content-Length: the exact byte size isn't known until every row has been streamed
            // (that's the whole point of not buffering it first) — the response is chunked-only.
            // Browsers lose a download-progress percentage as a result; accepted trade-off of
            // streaming vs. the OOM this fix exists to close.

            try
            {
                await WriteExportAsync(context.Response.BodyWriter, export, cancellationToken);

                // Success path only (spec-household-export-observability.md) — a mid-export fault
                // falls through to the catch block below and its own existing log calls instead;
                // this line never fires for a truncated/aborted response.
                stopwatch.Stop();
                logger.LogInformation(
                    "Household export succeeded for household {HouseholdId} in {DurationMs}ms: {@CollectionStats}",
                    householdId, stopwatch.ElapsedMilliseconds, export.Stats.Snapshot());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A mid-export DB fault or write failure must never present as a clean, complete
                // 200 (spec: abort-on-fault only — no trailing integrity checksum this iteration).
                // Headers/some bytes may already be flushed to the client by this point; Abort()
                // resets the connection at the transport level so the client observes a broken
                // transfer rather than a truncated-but-well-formed file.
                //
                // A client disconnecting mid-download surfaces here too (Kestrel doesn't always
                // wrap that as OperationCanceledException — e.g. a broken-pipe IOException), and
                // cancellationToken (HttpContext.RequestAborted) is already signalled by then. That
                // is not a server-side export failure, so it's logged at a lower level to keep
                // monitoring focused on faults this endpoint actually caused.
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInformation(ex, "Household export for household {HouseholdId} stopped because the client disconnected.", householdId);
                }
                else
                {
                    logger.LogError(ex, "Household export failed mid-stream for household {HouseholdId}; aborting the connection.", householdId);
                }

                context.Abort();
            }

            return Results.Empty;
        });

        return api;
    }

    // Rows flushed to the wire per Utf8JsonWriter.Flush()/PipeWriter.FlushAsync() pair while writing
    // one collection's array — matches HouseholdExportReader.PageSize (the same 500-row boundary the
    // DB side already paginates at), so the write side never buffers more than one DB page's worth
    // of serialized JSON regardless of how large the whole collection is. Flushing only once per
    // *entire* array (the original design) would silently re-buffer an unbounded amount of
    // serialized bytes in the PipeWriter for a large single collection — exactly the OOM shape this
    // fix exists to eliminate, just moved from "buffered domain objects" to "buffered JSON bytes"
    // (caught in adversarial review, not the original design). Internal (not private), and so are
    // WriteExportAsync/WriteArrayAsync below — EnergyTracker.Api.Tests already has InternalsVisibleTo
    // access (see the .csproj), letting HouseholdExportEndpointsWriteTests assert the flush cadence
    // directly against a real System.IO.Pipelines.Pipe instead of only inferring it from HTTP-level
    // behavior, which can't reliably distinguish "flushed periodically" from "buffered then flushed
    // once" once OS/transport-level buffering is in the mix.
    internal const int FlushEveryNItems = 500;

    // Utf8JsonWriter buffers into whatever IBufferWriter<byte> it's given (here, the response's
    // PipeWriter) — Utf8JsonWriter.Flush() only hands buffered bytes off to that IBufferWriter, it
    // does not itself push bytes over the wire. Each flush below is therefore a Utf8JsonWriter.Flush()
    // (moves bytes into the PipeWriter) followed by an explicit PipeWriter.FlushAsync() (actually
    // sends them) — done every FlushEveryNItems rows within a collection (not per-element, to keep
    // overhead low; not only at array-end, to keep peak buffered memory bounded), which also gives a
    // mid-collection fault an early point at which to abort before the whole response looks complete.
    internal static async Task WriteExportAsync(PipeWriter bodyWriter, HouseholdExportStream export, CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(bodyWriter);

        writer.WriteStartObject();
        writer.WriteString("formatVersion", export.FormatVersion);
        writer.WriteString("exportedAtUtc", export.ExportedAtUtc);

        writer.WritePropertyName("household");
        JsonSerializer.Serialize(writer, export.Household, SerializerOptions);

        await WriteArrayAsync(writer, bodyWriter, "householdMembers", export.HouseholdMembers, cancellationToken);

        writer.WritePropertyName("mainMeter");
        if (export.MainMeter is { } mainMeter)
        {
            JsonSerializer.Serialize(writer, mainMeter, SerializerOptions);
        }
        else
        {
            writer.WriteNullValue();
        }

        await WriteArrayAsync(writer, bodyWriter, "meterReadings", export.MeterReadings, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "meterRegressionPrompts", export.MeterRegressionPrompts, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "tariffs", export.Tariffs, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "events", export.Events, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "rooms", export.Rooms, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "powerPoints", export.PowerPoints, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "devices", export.Devices, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "smartPlugReadings", export.SmartPlugReadings, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "statusSnapshots", export.StatusSnapshots, cancellationToken);
        await WriteArrayAsync(writer, bodyWriter, "auditCorrections", export.AuditCorrections, cancellationToken);

        writer.WriteEndObject();
        writer.Flush();
        await bodyWriter.FlushAsync(cancellationToken);
    }

    private static async Task WriteArrayAsync<T>(
        Utf8JsonWriter writer, PipeWriter bodyWriter, string propertyName, IAsyncEnumerable<T> items, CancellationToken cancellationToken)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();

        var count = 0;
        await foreach (var item in items)
        {
            JsonSerializer.Serialize(writer, item, SerializerOptions);
            count++;
            if (count % FlushEveryNItems == 0)
            {
                writer.Flush();
                await bodyWriter.FlushAsync(cancellationToken);
            }
        }

        writer.WriteEndArray();
        writer.Flush();
        await bodyWriter.FlushAsync(cancellationToken);
    }
}
