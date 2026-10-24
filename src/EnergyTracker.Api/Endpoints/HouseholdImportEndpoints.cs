using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;

namespace EnergyTracker.Api.Endpoints;

public static class HouseholdImportEndpoints
{
    private const string NoHouseholdDetail = "The authenticated principal does not belong to a Household.";

    // Story 7.1's own live-verification export of one real (fairly small) household was already
    // 32 MB (Task 1) — the 20 MB cap SmartPlugImportEndpoints uses does not transfer here. 250 MB
    // is generous headroom while still bounding what a single upload can occupy on temp disk.
    // Kestrel's default MaxRequestBodySize (~28.6 MB) and FormOptions' default
    // MultipartBodyLengthLimit (128 MB) both sit below this and are raised to match, once, at the
    // composition root (Program.cs) — an in-handler IHttpMaxRequestBodySizeFeature override would
    // be too late, since Minimal API's IFormFile model binding reads the whole body before this
    // endpoint's delegate ever runs.
    internal const long MaxFileSizeBytes = 250L * 1024 * 1024;

    // Headroom above MaxFileSizeBytes for the Kestrel-level body-size limit (Program.cs), not the
    // app-level check below — the raw multipart request body (boundaries/headers) is always
    // somewhat larger than IFormFile.Length alone. Without this margin, an upload at or near
    // MaxFileSizeBytes gets rejected by Kestrel before this endpoint's delegate ever runs, so the
    // friendly 400 ProblemDetails response below could rarely fire in practice (Code Review, Story
    // 7.2 Pass 1).
    internal const long BodySizeHeadroomBytes = 5L * 1024 * 1024;

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

    public static RouteGroupBuilder MapHouseholdImportEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/household-import", async (
            IFormFile file,
            ICurrentHouseholdAccessor householdAccessor,
            ValidateHouseholdImport validateHouseholdImport,
            IHouseholdImportUploadRegistry uploadRegistry,
            HouseholdImportUploadSettings uploadSettings,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            return await ReceiveUploadAsync(
                file, householdId, validateHouseholdImport, uploadRegistry, uploadSettings, cancellationToken);
        })
        // Same reasoning as SmartPlugImportEndpoints' own upload endpoint — Minimal APIs attach
        // antiforgery metadata to any IFormFile-binding endpoint by default; this app has no
        // app.UseAntiforgery() middleware (session identity is a cookie the SPA never reads).
        .DisableAntiforgery()
        .RequireRateLimiting(RateLimitPolicies.Upload);

        api.MapPost("/household-import/{token:guid}/confirm", async (
            Guid token,
            ICurrentHouseholdAccessor householdAccessor,
            IHouseholdImportUploadRegistry uploadRegistry,
            IBackgroundJobRepository backgroundJobRepository,
            IBackgroundJobLifecycle backgroundJobLifecycle,
            IBackgroundJobQueue jobQueue,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

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

            // AD-24: one instance with one worker. Without this, two confirmed restores for the same
            // Household could run as genuinely concurrent, unguarded delete+insert transactions. A DB
            // check followed by an enqueue (mirroring AD-12's "at most one open X per Y" precedent), so
            // it serialises through the single consumer; Story 11.14 adds a database guard for the
            // remaining race. Checked after consuming the token — a token can only ever be
            // consumed once regardless, so this only rejects a second, genuinely different upload
            // racing an already-in-flight restore for the same Household, never a retry of the
            // same token (Code Review, Story 7.2 Pass 1).
            //
            // Story 11.2: a restore orphaned by a restart/crash used to hold this 409 forever. Rows
            // whose heartbeat is older than 5 minutes are failed first (a restore that is still
            // heartbeating keeps the guard for its whole run).
            await backgroundJobLifecycle.FailStaleAsync(householdId, cancellationToken);
            var restoreJobs = await backgroundJobRepository.ListByJobTypeAsync(householdId, JobTypes.RestoreHouseholdData, cancellationToken);
            if (restoreJobs.Any(j => j.Status is BackgroundJobStatus.Queued or BackgroundJobStatus.Processing))
            {
                if (File.Exists(reference.TempFilePath))
                {
                    File.Delete(reference.TempFilePath);
                }

                return Results.Problem(
                    detail: "A restore is already in progress for this Household — wait for it to finish before starting another.",
                    statusCode: StatusCodes.Status409Conflict);
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
            catch
            {
                // Same enqueue-failure cleanup shape as SmartPlugImportEndpoints' upload path —
                // the token is already consumed above, so a retry means uploading again.
                if (File.Exists(reference.TempFilePath))
                {
                    File.Delete(reference.TempFilePath);
                }

                throw;
            }

            // 202 Accepted — the client learns completion by polling GET /api/jobs/{id}, exactly
            // like every other async job in this codebase (AD-6). No backend change needed there
            // (JobEndpoints.cs is already generic across JobType).
            return Results.Accepted($"/api/jobs/{jobId}", new HouseholdImportConfirmResponse(jobId));
        }).RequireRateLimiting(RateLimitPolicies.Upload);

        return api;
    }

    // Story 11.10 (audit S4): the upload is streamed to its temp file first and validated from there,
    // so it is never held whole in memory. That makes the file exist before validation, so every exit
    // that does not end in a successful Register deletes it (a 400, any exception, a cancelled request).
    // One try/finally owns that rule. Internal so each cleanup path has a test of its own.
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

            // Pure read/parse — no DB write of any kind happens in this phase (AC #2's "never
            // partially applied" structurally, not by convention).
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

// MeterRegressionPrompts was missing from this record entirely (Code Review, Story 7.2 Pass 1) —
// eleven of the twelve in-scope entity categories were shown to the user before they confirm
// "replace all data"; regression prompt count was silently absent.
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
