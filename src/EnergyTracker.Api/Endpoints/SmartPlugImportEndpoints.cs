using EnergyTracker.Application;
using EnergyTracker.Application.Ports;

namespace EnergyTracker.Api.Endpoints;

public static class SmartPlugImportEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";
    private static readonly string[] AllowedExtensions = [".xlsx", ".csv"];

    // Real Eve Home/Meross exports run hundreds of KB (Task 3); 20 MB is generous headroom while
    // still bounding how much a single upload can occupy on temp disk and in the single shared
    // background-processing loop.
    private const long MaxFileSizeBytes = 20 * 1024 * 1024;

    // Same shape as MeterReadingEndpoints.TryGetHouseholdId — copied rather than referenced
    // across files (that helper is private to its own class).
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

    public static RouteGroupBuilder MapSmartPlugImportEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/smart-plug-imports", async (
            IFormFile file,
            ICurrentHouseholdAccessor householdAccessor,
            IBackgroundJobQueue jobQueue,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            var extension = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Problem(
                    detail: $"Unsupported file type '{extension}'. Only .xlsx (Eve Home) and .csv (Meross) exports are accepted.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (file.Length > MaxFileSizeBytes)
            {
                return Results.Problem(
                    detail: $"File is too large ({file.Length} bytes). The maximum accepted size is {MaxFileSizeBytes} bytes.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // A short-lived temp location the same process's job-processing loop reads back —
            // API and job worker are one process, one container in both environments (AD-6), so
            // plain temp-disk storage (rather than a separate blob-storage adapter) is sufficient.
            // The payload below carries only this path + metadata, never the file bytes
            // themselves — Azure Storage Queue caps a message at 64 KB.
            var tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{extension}");
            await using (var tempFileStream = File.Create(tempFilePath))
            {
                await file.CopyToAsync(tempFileStream, cancellationToken);
            }

            var jobId = Guid.NewGuid();
            var smartPlugImportId = Guid.NewGuid();
            var payload = new ProcessSmartPlugImportPayload(smartPlugImportId, tempFilePath, file.FileName);
            try
            {
                await jobQueue.EnqueueAsync(
                    new JobEnvelope<ProcessSmartPlugImportPayload>(jobId, householdId, JobTypes.ProcessSmartPlugImport, payload),
                    cancellationToken);
            }
            catch
            {
                if (File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }

                throw;
            }

            // 202 Accepted — no parsing happens synchronously (AC #1); the client learns
            // completion by polling GET /api/jobs/{id} (AC #2).
            return Results.Accepted($"/api/jobs/{jobId}", new SmartPlugImportUploadResponse(jobId));
        })
        // Minimal APIs attach antiforgery metadata to any IFormFile-binding endpoint by default
        // (CSRF hardening for form posts) — this app has no app.UseAntiforgery() middleware
        // (session identity is a cookie the SPA never reads, not a browser form), so the
        // endpoint would 500 on every request without this. Auth (RequireAuthorization on the
        // /api group) plus SameSite=Lax already protects the upload the way this app protects
        // every other write endpoint.
        .DisableAntiforgery();

        return api;
    }
}

public record SmartPlugImportUploadResponse(Guid JobId);
