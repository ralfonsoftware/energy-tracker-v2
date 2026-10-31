using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdImportEndpoints
{
    // 250 MB bounds what one upload can occupy on temp disk (a small real export was already 32 MB). The Kestrel body limit is
    // raised for this route only (Program.cs); the form limit is raised globally. ADR 0004.
    internal const long MaxFileSizeBytes = 250L * 1024 * 1024;

    // Headroom for the multipart framing, which makes the raw body larger than IFormFile.Length. ADR 0004.
    internal const long BodySizeHeadroomBytes = 5L * 1024 * 1024;

    public static RouteGroupBuilder MapHouseholdImportEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup(string.Empty).RequireHousehold();
        household.MapPost("/household-import", async (
            IFormFile file,
            ICurrentHouseholdAccessor householdAccessor,
            ValidateHouseholdImport validateHouseholdImport,
            IHouseholdImportUploadRegistry uploadRegistry,
            HouseholdImportUploadSettings uploadSettings,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            return await ReceiveUploadAsync(
                file, householdId, validateHouseholdImport, uploadRegistry, uploadSettings, cancellationToken);
        })
        // Same reasoning as SmartPlugImportEndpoints' own upload endpoint — Minimal APIs attach
        // antiforgery metadata to any IFormFile-binding endpoint by default; this app has no
        // app.UseAntiforgery() middleware (session identity is a cookie the SPA never reads).
        .DisableAntiforgery()
        .RequireRateLimiting(RateLimitPolicies.Upload);

        household.MapPost("/household-import/{token:guid}/confirm", async (
            Guid token,
            ICurrentHouseholdAccessor householdAccessor,
            IHouseholdImportUploadRegistry uploadRegistry,
            IBackgroundJobRepository backgroundJobRepository,
            IBackgroundJobLifecycle backgroundJobLifecycle,
            IBackgroundJobQueue jobQueue,
            CancellationToken cancellationToken) =>
        {
            var householdId = householdAccessor.RequireHouseholdId();

            // Atomically verifies the token belongs to THIS Household and removes it in one step
            // — a guessed/leaked token from a different Household 404s exactly like an unknown
            // token (IDOR guard, Task 5).
            var reference = uploadRegistry.Consume(token, householdId);
            if (reference is null)
            {
                return Results.Problem(
                    detail: $"No pending import '{token}' found for this Household — it may have expired or already been confirmed.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            // AD-24: two confirmed restores for one Household must not run concurrently. A DB check then enqueue (AD-12 precedent) is the
            // fast path; the filtered unique index on BackgroundJobs closes the race (AD-6). Stale rows (heartbeat older than 5 minutes) are failed first.
            await backgroundJobLifecycle.FailStaleAsync(householdId, cancellationToken);
            var restoreJobs = await backgroundJobRepository.ListByJobTypeAsync(householdId, JobTypes.RestoreHouseholdData, cancellationToken);
            if (restoreJobs.Any(j => j.Status is BackgroundJobStatus.Queued or BackgroundJobStatus.Processing))
            {
                DeleteTempFile(reference.TempFilePath);
                return RestoreAlreadyInProgress();
            }

            var jobId = Guid.NewGuid();
            var payload = new RestoreHouseholdDataPayload(reference.TempFilePath, reference.OriginalFileName);
            try
            {
                await jobQueue.EnqueueAsync(
                    new JobEnvelope<RestoreHouseholdDataPayload>(
                        jobId, householdId, JobTypes.RestoreHouseholdData, payload,
                        QueuedByHouseholdMemberId: householdAccessor.HouseholdMemberId,
                        OriginalFileName: reference.OriginalFileName),
                    cancellationToken);
            }
            catch (ActiveJobAlreadyExistsException)
            {
                // Lost the race the early check cannot see: the database index rejected the
                // row before anything was sent, so the consumed upload's file is deleted like on the
                // early-check path and the answer is the same 409.
                DeleteTempFile(reference.TempFilePath);
                return RestoreAlreadyInProgress();
            }
            catch
            {
                // Same enqueue-failure cleanup shape as SmartPlugImportEndpoints' upload path —
                // the token is already consumed above, so a retry means uploading again.
                DeleteTempFile(reference.TempFilePath);
                throw;
            }

            // 202 Accepted — the client learns completion by polling GET /api/jobs/{id}, exactly
            // like every other async job in this codebase (AD-6). No backend change needed there
            // (JobEndpoints.cs is already generic across JobType).
            return Results.Accepted($"/api/jobs/{jobId}", new HouseholdImportConfirmResponse(jobId));
        }).RequireRateLimiting(RateLimitPolicies.Upload);

        return api;
    }

    // The one 409 for "a restore is already active", shared by the early check and the lost-race catch so
    // the two answers cannot drift.
    private static IResult RestoreAlreadyInProgress() => Results.Problem(
        detail: "A restore is already in progress for this Household — wait for it to finish before starting another.",
        statusCode: StatusCodes.Status409Conflict);

    // Every confirm path that does not end in an enqueued job deletes the consumed upload's file.
    private static void DeleteTempFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // Streamed to a temp file and validated from there. The file exists before validation, so every exit that does not
    // register it deletes it; one try/finally owns that.
    internal static async Task<IResult> ReceiveUploadAsync(
        IFormFile file,
        Guid householdId,
        ValidateHouseholdImport validateHouseholdImport,
        IHouseholdImportUploadRegistry uploadRegistry,
        HouseholdImportUploadSettings uploadSettings,
        CancellationToken cancellationToken)
    {
        if (file.Length > uploadSettings.MaxFileSizeBytes)
        {
            return Results.Problem(
                detail: $"File is too large ({file.Length} bytes). The maximum accepted size is {uploadSettings.MaxFileSizeBytes} bytes.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Short-lived temp location the confirm step (or the async restore job, once confirmed) reads
        // back — mirrors SmartPlugImportEndpoints' own upload path. The opaque token below is what the
        // client holds, never this path. Process-local temp file: AD-24 (single instance).
        var tempFilePath = Path.Combine(uploadSettings.TempDirectory, $"{Guid.NewGuid()}.json");
        var registered = false;
        try
        {
            await using (var target = new FileStream(
                tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var source = file.OpenReadStream())
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            // Pure read/parse — no DB write of any kind happens in this phase (never partially
            // applied, structurally rather than by convention).
            HouseholdImportValidationResult result;
            await using (var validated = new FileStream(
                tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.SequentialScan))
            {
                result = validateHouseholdImport.Execute(validated, cancellationToken);
            }

            if (!result.IsValid)
            {
                return Results.Problem(
                    detail: "The uploaded file failed validation against the v2 export format.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["failures"] = result.Failures });
            }

            // A client that left while the file validated must not replace the Household's earlier, good pending upload.
            cancellationToken.ThrowIfCancellationRequested();
            var token = Guid.NewGuid();
            uploadRegistry.Register(token, new HouseholdImportUploadReference(householdId, tempFilePath, file.FileName));
            registered = true;

            return Results.Ok(new HouseholdImportValidationResponse(token, BuildSummary(result.Inventory!)));
        }
        finally
        {
            if (!registered)
            {
                TryDeleteTempFile(tempFilePath);
            }
        }
    }

    // Best-effort, as the registry's own delete: a locked or already-gone file must never replace the
    // response (or the original exception) with a cleanup failure.
    private static void TryDeleteTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // memberDisplayNames feeds the "People in this backup" panel shown after the restore (AD-25).
    // The temp file is gone by then and the job status carries no result payload, so the names ride
    // along in this validate response instead. Names are untrusted file content: trimmed, blanks
    // dropped, file order and duplicates kept; the client renders them as plain text only.
    private static HouseholdImportSummary BuildSummary(HouseholdImportInventory inventory) => new(
        inventory.HouseholdMembers,
        inventory.HasMainMeter,
        inventory.MeterReadings,
        inventory.MeterRegressionPrompts,
        inventory.Tariffs,
        inventory.Events,
        inventory.Rooms,
        inventory.PowerPoints,
        inventory.Devices,
        inventory.SmartPlugReadings,
        inventory.StatusSnapshots,
        inventory.AuditCorrections,
        inventory.MemberDisplayNames);
}

public record HouseholdImportValidationResponse(Guid Token, HouseholdImportSummary Summary);

// Where uploaded import files are spooled and the largest one accepted. Production registers the OS temp
// directory and MaxFileSizeBytes (Program.cs); an API test host replaces the registration so it can count
// only its own files and reach the size check without a 250 MB body. Not a configuration key.
public record HouseholdImportUploadSettings(string TempDirectory, long MaxFileSizeBytes);

// The summary lists every in-scope entity category the user sees before confirming "replace all data".
public record HouseholdImportSummary(
    int HouseholdMembers,
    bool HasMainMeter,
    int MeterReadings,
    int MeterRegressionPrompts,
    int Tariffs,
    int Events,
    int Rooms,
    int PowerPoints,
    int Devices,
    int SmartPlugReadings,
    int StatusSnapshots,
    int AuditCorrections,
    IReadOnlyList<string> MemberDisplayNames);

public record HouseholdImportConfirmResponse(Guid JobId);
