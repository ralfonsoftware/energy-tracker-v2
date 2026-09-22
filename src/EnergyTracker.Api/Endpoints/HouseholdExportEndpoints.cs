using System.Text.Json;
using EnergyTracker.Application;
using EnergyTracker.Application.Ports;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdExportEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Web defaults (camelCase) — matches every other endpoint's implicit Results.Ok() serialization,
    // since this response is built manually via Results.File instead (Task 4's Content-Disposition
    // requirement, see below).
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
            ICurrentHouseholdAccessor householdAccessor,
            ExportHouseholdData exportHouseholdData,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            var export = await exportHouseholdData.ExecuteAsync(householdId, cancellationToken);
            var json = JsonSerializer.SerializeToUtf8Bytes(export, SerializerOptions);
            // Derived from the export's own ExportedAtUtc, not a fresh DateTimeOffset.UtcNow call —
            // keeps the downloaded filename's date and the document's own exportedAtUtc field from
            // disagreeing if the request happens to straddle a UTC-midnight boundary.
            var fileName = $"energy-tracker-export-{export.ExportedAtUtc:yyyy-MM-dd}.json";
            // Results.File with a fileDownloadName sets Content-Disposition: attachment so a
            // browser fetch + click-through downloads a real file (Task 4) rather than navigating
            // to raw JSON.
            return Results.File(json, "application/json", fileName);
        });

        return api;
    }
}
