using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

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
                    new JobEnvelope<ProcessSmartPlugImportPayload>(
                        jobId, householdId, JobTypes.ProcessSmartPlugImport, payload,
                        QueuedByHouseholdMemberId: householdAccessor.HouseholdMemberId,
                        OriginalFileName: file.FileName),
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

        api.MapPost("/smart-plug-imports/{id:guid}/power-point-mapping", async (
            Guid id,
            MapSmartPlugImportRequest request,
            ICurrentHouseholdAccessor householdAccessor,
            MapSmartPlugImportToPowerPoint mapSmartPlugImportToPowerPoint,
            CancellationToken cancellationToken) =>
        {
            // AD-3's query filter alone would already stop a cross-Household id from resolving,
            // but every sibling endpoint (RenamePowerPoint/MovePowerPoint included, the exact
            // precedent this handler follows) still calls TryGetHouseholdId first so a principal
            // with no Household gets a 403 rather than a misleading 404.
            if (!TryGetHouseholdId(householdAccessor, out _, out var forbidden))
            {
                return forbidden;
            }

            try
            {
                await mapSmartPlugImportToPowerPoint.ExecuteAsync(id, request.PowerPointId, cancellationToken);
                return Results.Ok(new SmartPlugImportMappingResponse(id, SmartPlugImportStatus.Completed.ToString().ToLowerInvariant()));
            }
            catch (SmartPlugImportNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (TaggingScaffoldNotFoundException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (SmartPlugImportValidationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (TaggingScaffoldParentArchivedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/smart-plug-import-jobs", async (
            ICurrentHouseholdAccessor householdAccessor,
            ListSmartPlugImportJobs listSmartPlugImportJobs,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            var jobs = await listSmartPlugImportJobs.ExecuteAsync(householdId, cancellationToken);
            return Results.Ok(jobs.Select(ToJobHistoryResponse).ToList());
        });

        return api;
    }

    private static SmartPlugImportJobHistoryResponse ToJobHistoryResponse(SmartPlugImportJobResult result) => new(
        result.JobId,
        result.FileName,
        ToStateString(result.State),
        result.QueuedByDisplayName,
        result.QueuedAtUtc,
        result.CompletedAtUtc,
        result.ErrorMessage,
        result.SmartPlugImportId,
        result.DeviceTag,
        result.Gaps.Select(ToGapDto).ToList());

    // camelCase matching System.Text.Json's default naming policy (e.g. NeedsMapping ->
    // "needsMapping") — deliberately not the ToLowerInvariant() convention this file's sibling
    // endpoints use for BackgroundJobStatus/SmartPlugImportStatus, since those concatenate into
    // unreadable all-lowercase strings ("awaitingpowerpointmapping") that this DTO's own six-
    // state contract (Task 4) spells out in camelCase explicitly.
    private static string ToStateString(SmartPlugImportJobState state) =>
        char.ToLowerInvariant(state.ToString()[0]) + state.ToString()[1..];

    private static SmartPlugImportGapDto ToGapDto(SmartPlugImportGap gap) => new(
        gap.StartDate, gap.EndDate, gap.Treatment.ToString().ToLowerInvariant(), gap.EstimatedTotalKwh);
}

public record SmartPlugImportUploadResponse(Guid JobId);

public record MapSmartPlugImportRequest(Guid PowerPointId);

public record SmartPlugImportMappingResponse(Guid Id, string Status);

public record SmartPlugImportJobHistoryResponse(
    Guid JobId,
    string? FileName,
    string State,
    string? QueuedByDisplayName,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorMessage,
    Guid? SmartPlugImportId,
    string? DeviceTag,
    IReadOnlyList<SmartPlugImportGapDto> Gaps);
