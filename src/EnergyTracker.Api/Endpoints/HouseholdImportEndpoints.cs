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
            CancellationToken cancellationToken) =>
        {
            if (!TryGetHouseholdId(householdAccessor, out var householdId, out var forbidden))
            {
                return forbidden;
            }

            if (file.Length > MaxFileSizeBytes)
            {
                return Results.Problem(
                    detail: $"File is too large ({file.Length} bytes). The maximum accepted size is {MaxFileSizeBytes} bytes.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            string json;
            await using (var stream = file.OpenReadStream())
            {
                using var reader = new StreamReader(stream);
                json = await reader.ReadToEndAsync(cancellationToken);
            }

            // Pure read/parse — no DB write of any kind happens in this phase (AC #2's "never
            // partially applied" structurally, not by convention).
            var result = validateHouseholdImport.Execute(json);
            if (!result.IsValid)
            {
                return Results.Problem(
                    detail: "The uploaded file failed validation against the v2 export format.",
                    statusCode: StatusCodes.Status400BadRequest,
                    extensions: new Dictionary<string, object?> { ["failures"] = result.Failures });
            }

            // Short-lived temp location the confirm step below (or the async restore job, once
            // confirmed) reads back — mirrors SmartPlugImportEndpoints' own upload path. The
            // opaque token below is what the client holds, never this path.
            var tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
            await File.WriteAllTextAsync(tempFilePath, json, cancellationToken);

            var token = Guid.NewGuid();
            uploadRegistry.Register(token, new HouseholdImportUploadReference(householdId, tempFilePath, file.FileName));

            return Results.Ok(new HouseholdImportValidationResponse(token, BuildSummary(result.Data!)));
        })
        // Same reasoning as SmartPlugImportEndpoints' own upload endpoint — Minimal APIs attach
        // antiforgery metadata to any IFormFile-binding endpoint by default; this app has no
        // app.UseAntiforgery() middleware (session identity is a cookie the SPA never reads).
        .DisableAntiforgery();

        api.MapPost("/household-import/{token:guid}/confirm", async (
            Guid token,
            ICurrentHouseholdAccessor householdAccessor,
            IHouseholdImportUploadRegistry uploadRegistry,
            IBackgroundJobRepository backgroundJobRepository,
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

            // AD-6's job queue has exactly one worker per process, but the Azure Storage Queue
            // provider can run multiple worker processes/replicas polling the same shared queue —
            // without this, two confirmed restores for the same Household could run as genuinely
            // concurrent, unguarded delete+insert transactions. Checked against the DB (not an
            // in-memory lock) so it holds across replicas, mirroring AD-12's "at most one open X
            // per Y" precedent. Checked after consuming the token — a token can only ever be
            // consumed once regardless, so this only rejects a second, genuinely different upload
            // racing an already-in-flight restore for the same Household, never a retry of the
            // same token (Code Review, Story 7.2 Pass 1).
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
        });

        return api;
    }

    private static HouseholdImportSummary BuildSummary(HouseholdExportResult data) => new(
        data.HouseholdMembers.Count,
        data.MainMeter is not null,
        data.MeterReadings.Count,
        data.MeterRegressionPrompts.Count,
        data.Tariffs.Count,
        data.Events.Count,
        data.Rooms.Count,
        data.PowerPoints.Count,
        data.Devices.Count,
        data.SmartPlugReadings.Count,
        data.StatusSnapshots.Count,
        data.AuditCorrections.Count);
}

public record HouseholdImportValidationResponse(Guid Token, HouseholdImportSummary Summary);

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
    int AuditCorrections);

public record HouseholdImportConfirmResponse(Guid JobId);
