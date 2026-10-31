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

    // Web defaults (camelCase) — matches every other endpoint's implicit Results.Ok() serialization.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapHouseholdExportEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        household.MapGet("/household-export", async (
            HttpContext context,
            ICurrentHouseholdAccessor householdAccessor,
            ExportHouseholdData exportHouseholdData,
            ILogger<LogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            // Spans the whole request, not just the write: a slow reader-side query is as much a danger signal as a slow write.
            var stopwatch = Stopwatch.StartNew();
            var export = await exportHouseholdData.ExecuteAsync(householdId, cancellationToken);

            // Derived from the export's own ExportedAtUtc, not a fresh clock read —
            // keeps the downloaded filename's date and the document's own exportedAtUtc field from
            // disagreeing if the request happens to straddle a UTC-midnight boundary.
            var fileName = $"energy-tracker-export-{export.ExportedAtUtc:yyyy-MM-dd}.json";
            context.Response.ContentType = "application/json";
            context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
            // No Content-Length: the size is unknown until the last row is streamed, so the download shows no progress percentage.
            try
            {
                await WriteExportAsync(context.Response.BodyWriter, export, cancellationToken);

                // Success path only: a mid-export fault falls through to the catch block, so this never logs a truncated response.
                stopwatch.Stop();
                logger.LogInformation(
                    "Household export succeeded for household {HouseholdId} in {DurationMs}ms: {@CollectionStats}",
                    householdId, stopwatch.ElapsedMilliseconds, export.Stats.Snapshot());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A mid-export fault must never look like a clean 200: Abort() resets the connection so the client sees a broken transfer.
                // A client disconnect surfaces here too and is not a server fault, so it logs at a lower level.
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
        }).RequireRateLimiting(RateLimitPolicies.Export);

        return api;
    }

    // Rows flushed per Utf8JsonWriter/PipeWriter flush pair, matching HouseholdExportReader.PageSize, so the write side never
    // buffers more than one DB page of JSON. Internal so HouseholdExportEndpointsWriteTests can assert the cadence on a real Pipe.
    internal const int FlushEveryNItems = 500;

    // Utf8JsonWriter.Flush() only hands bytes to the PipeWriter; PipeWriter.FlushAsync() sends them. Flushing every
    // FlushEveryNItems rows bounds peak memory and gives a mid-collection fault an early point to abort.
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
