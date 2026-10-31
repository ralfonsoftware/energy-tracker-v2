using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class SmartPlugImportEndpoints
{
    private static readonly string[] AllowedExtensions = [".xlsx", ".csv"];

    // Real Eve Home/Meross exports run hundreds of KB (Task 3); 20 MB is generous headroom while
    // still bounding how much a single upload can occupy on temp disk and in the single shared
    // background-processing loop.
    private const long MaxFileSizeBytes = 20 * 1024 * 1024;

    public static RouteGroupBuilder MapSmartPlugImportEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        household.MapPost("/smart-plug-imports", async (
            IFormFile file,
            ICurrentHouseholdAccessor householdAccessor,
            IBackgroundJobQueue jobQueue,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

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

            // Short-lived temp file the job loop of this same process reads back (AD-6); the queue message carries only the path and
            // metadata, never the bytes (a message caps at 64 KB). Process-local on purpose: AD-24.
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
        // Minimal APIs attach antiforgery metadata to IFormFile endpoints and this app has no UseAntiforgery() middleware, so the
        // endpoint would 500 without this. Auth and SameSite=Lax protect the upload like every other write.
        .DisableAntiforgery()
        .RequireRateLimiting(RateLimitPolicies.Upload);

        household.MapPost("/smart-plug-imports/{id:guid}/power-point-mapping", async (
            Guid id,
            MapSmartPlugImportRequest request,
            MapSmartPlugImportToPowerPoint mapSmartPlugImportToPowerPoint,
            CancellationToken cancellationToken) =>
        {
            // Behind RequireHousehold like the sibling endpoints, so a principal with no Household gets 403 instead of a misleading 404.
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

        household.MapGet("/smart-plug-import-jobs", async (
            ICurrentHouseholdAccessor householdAccessor,
            ListSmartPlugImportJobs listSmartPlugImportJobs,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            var jobs = await listSmartPlugImportJobs.ExecuteAsync(householdId, cancellationToken);
            return Results.Ok(jobs.Select(ToJobHistoryResponse).ToList());
        });

        // Manual cleanup (Story 3.10): deleteAll=false deletes records older than 30 days, true deletes everything. Eligible in all
        // six states, unlike the lazy sweep. Runs as an async job (AD-6): a Household's total work can exceed the ~240s ingress limit.
        household.MapDelete("/smart-plug-import-jobs", async (
            ICurrentHouseholdAccessor householdAccessor,
            IBackgroundJobQueue jobQueue,
            IBackgroundJobRepository backgroundJobRepository,
            IBackgroundJobLifecycle backgroundJobLifecycle,
            bool deleteAll,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            // Reuse the active cleanup job instead of enqueueing a second one (double click, second tab, retry). The filtered unique index
            // on BackgroundJobs is the guarantee (AD-6); the loser answers with the winner's job id. Stale rows are failed first.
            await backgroundJobLifecycle.FailStaleAsync(householdId, cancellationToken);
            var existingCleanupJobs = await backgroundJobRepository.ListByJobTypeAsync(
                householdId, JobTypes.CleanUpSmartPlugImportJobs, cancellationToken);
            var activeCleanupJob = existingCleanupJobs.FirstOrDefault(
                job => job.Status is BackgroundJobStatus.Queued or BackgroundJobStatus.Processing);
            if (activeCleanupJob is not null)
            {
                return Results.Accepted($"/api/jobs/{activeCleanupJob.Id}", new SmartPlugImportJobCleanupResponse(activeCleanupJob.Id));
            }

            var jobId = Guid.NewGuid();
            try
            {
                await jobQueue.EnqueueAsync(
                    new JobEnvelope<CleanUpSmartPlugImportJobsPayload>(
                        jobId, householdId, JobTypes.CleanUpSmartPlugImportJobs, new CleanUpSmartPlugImportJobsPayload(deleteAll),
                        QueuedByHouseholdMemberId: householdAccessor.HouseholdMemberId),
                    cancellationToken);
            }
            catch (ActiveJobAlreadyExistsException ex)
            {
                // Lost the race the early check cannot see: the same idempotent answer, with the id of the
                // cleanup that won.
                return Results.Accepted($"/api/jobs/{ex.ActiveJobId}", new SmartPlugImportJobCleanupResponse(ex.ActiveJobId));
            }

            return Results.Accepted($"/api/jobs/{jobId}", new SmartPlugImportJobCleanupResponse(jobId));
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

    // camelCase to match System.Text.Json's naming policy, not ToLowerInvariant(), which would give "awaitingpowerpointmapping".
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

public record SmartPlugImportJobCleanupResponse(Guid JobId);
