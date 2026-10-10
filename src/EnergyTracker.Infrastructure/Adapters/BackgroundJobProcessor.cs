using System.Text.Json;
using EnergyTracker.Application;
using EnergyTracker.Domain;
using EnergyTracker.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// Shared dequeue-side processing loop body for both queue adapters' hosted BackgroundServices.
// The BackgroundJob row is DB-persisted (not in-memory: Container Apps can scale to zero/multiple
// replicas between enqueue and a client's next poll) and inserted as Queued at enqueue time; every
// status change after that goes through IBackgroundJobLifecycle (AD-6, amended 2026-10-10): one
// conditional update per transition, guarded by the StartedAtUtc ownership token. This class
// resolves the job's use case by JobType, keeps HeartbeatAtUtc fresh while it runs, and ends the
// job Completed/Failed. It never inserts or resurrects a row.
public class BackgroundJobProcessor(
    IServiceScopeFactory scopeFactory, JobLifecycleTimings timings, ILogger<BackgroundJobProcessor> logger)
{
    private const int TransitionAttempts = 3;

    internal async Task ProcessAsync(JobMessage message, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        // AD-3's job-processing resolution path: set before anything downstream resolves
        // EnergyTrackerDbContext, so the very first query in this scope already sees the right
        // Household via the standard query filter.
        services.GetRequiredService<JobHouseholdContext>().HouseholdId = message.HouseholdId;

        var lifecycle = services.GetRequiredService<IBackgroundJobLifecycle>();

        var token = await RetryTransientAsync(
            () => lifecycle.TryStartAsync(message.JobId, cancellationToken), "start", message.JobId, cancellationToken);
        if (token is null)
        {
            // Missing, terminal, or owned by a live run: nothing to do. Returning normally is the
            // queue adapters' "delete the message" signal (Azure) / "move on" (in-process).
            logger.LogInformation(
                "Background job {JobId} is missing, already finished, or owned by a live run; skipping delivery.", message.JobId);
            return;
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = RunHeartbeatAsync(message, token.Value, heartbeatCts.Token);

        bool completed;
        string? failureMessage = null;
        try
        {
            try
            {
                await ExecuteJobAsync(services, message, cancellationToken);
                completed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown/redeploy, not a processing failure — leave the job Processing (never
                // Failed) so a redelivered message, or the stale check once its heartbeat lapses, still
                // finds it recoverable. An OperationCanceledException with the stopping token NOT
                // cancelled (an HttpClient or command timeout) falls through to the generic catch below.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background job {JobId} ({JobType}) failed", message.JobId, message.JobType);
                completed = false;
                // SmartPlugImportValidationException's message is deliberately user-facing (bad file
                // content/name) — anything else is an unexpected internal failure whose raw .Message
                // (file paths, DB errors, etc.) must never be forwarded verbatim to the client that
                // polls GET /api/jobs/{id}. A generic failure leaves ErrorMessage null: this backend has
                // no concept of the client's locale, so the client supplies its own localized fallback
                // (Round-4 incident fix). Stable codes (job-interrupted, ...) are the third kind of
                // value and are written by the lifecycle, never from an exception message.
                failureMessage = ex is SmartPlugImportValidationException or HouseholdImportValidationException ? ex.Message : null;
            }
        }
        finally
        {
            // Always stopped and awaited, on every exit (completion, failure, shutdown), and before
            // the terminal transition so a late tick cannot race it.
            await StopHeartbeatAsync(heartbeatCts, heartbeat);
        }

        // Terminal transitions pass CancellationToken.None: they are single short statements, and a
        // stopping token that has just been cancelled must not leave a job that already finished
        // (e.g. a restore that committed) Processing, to be failed later as "interrupted" while its
        // data was in fact replaced. A transient database error is retried a few times for the same
        // reason; only an outage that outlasts the retries leaves the row to the stale check.
        var transitioned = completed
            ? await RetryTransientAsync(
                () => lifecycle.TryCompleteAsync(message.JobId, token.Value, CancellationToken.None), "complete", message.JobId, CancellationToken.None)
            : await RetryTransientAsync(
                () => lifecycle.TryFailAsync(message.JobId, token.Value, failureMessage, CancellationToken.None), "fail", message.JobId, CancellationToken.None);
        if (!transitioned)
        {
            // Cleanup deleted the row mid-run, or the job was failed as stale (e.g. a database
            // outage longer than the stale window). Not an error: nothing is resurrected or overwritten.
            logger.LogWarning(
                "Background job {JobId} ({JobType}) no longer owns its row; the final transition was dropped.", message.JobId, message.JobType);
        }
    }

    private async Task ExecuteJobAsync(IServiceProvider services, JobMessage message, CancellationToken cancellationToken)
    {
        switch (message.JobType)
        {
            case JobTypes.ProcessSmartPlugImport:
                var payload = JsonSerializer.Deserialize<ProcessSmartPlugImportPayload>(message.PayloadJson)
                    ?? throw new InvalidOperationException($"Job {message.JobId}: payload deserialized to null.");
                var useCase = services.GetRequiredService<ProcessSmartPlugImport>();
                await useCase.ExecuteAsync(message.HouseholdId, message.JobId, payload, cancellationToken);
                break;
            case JobTypes.CleanUpSmartPlugImportJobs:
                var cleanupPayload = JsonSerializer.Deserialize<CleanUpSmartPlugImportJobsPayload>(message.PayloadJson)
                    ?? throw new InvalidOperationException($"Job {message.JobId}: payload deserialized to null.");
                var cleanUpUseCase = services.GetRequiredService<CleanUpSmartPlugImportJobs>();
                var deletedCount = await cleanUpUseCase.ExecuteAsync(message.HouseholdId, cleanupPayload.DeleteAll, cancellationToken);
                logger.LogInformation("Cleanup job {JobId} deleted {DeletedCount} row(s)", message.JobId, deletedCount);
                break;
            case JobTypes.CorrelateEvent:
                var correlatePayload = JsonSerializer.Deserialize<CorrelateEventPayload>(message.PayloadJson)
                    ?? throw new InvalidOperationException($"Job {message.JobId}: payload deserialized to null.");
                var correlateUseCase = services.GetRequiredService<CorrelateEvent>();
                await correlateUseCase.ExecuteAsync(message.HouseholdId, correlatePayload, cancellationToken);
                break;
            case JobTypes.RestoreHouseholdData:
                var restorePayload = JsonSerializer.Deserialize<RestoreHouseholdDataPayload>(message.PayloadJson)
                    ?? throw new InvalidOperationException($"Job {message.JobId}: payload deserialized to null.");
                var restoreUseCase = services.GetRequiredService<RestoreHouseholdData>();
                await restoreUseCase.ExecuteAsync(message.HouseholdId, restorePayload, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unknown JobType '{message.JobType}'.");
        }
    }

    // A transient database error on a lifecycle statement must not strand a job: a failed start drops
    // the in-process message and leaves a Queued row that blocks the Household until a restart, a
    // failed terminal transition reports a finished job as interrupted. Bounded and cancellable by
    // the caller's token; a cancelled token (shutdown) is never retried.
    private async Task<T> RetryTransientAsync<T>(Func<Task<T>> action, string transition, Guid jobId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < TransitionAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex, "Background job {JobId}: {Transition} transition failed (attempt {Attempt} of {Attempts}); retrying.",
                    jobId, transition, attempt, TransitionAttempts);
                await Task.Delay(timings.TransitionRetryDelay * attempt, cancellationToken);
            }
        }
    }

    // Each tick runs in its own scope with its own DbContext/connection: the job's own scope may be
    // inside a long transaction (restore: one transaction, 120 s command timeout, chunked), and a
    // heartbeat that shared it would block behind or inside that transaction. A heartbeat problem is
    // logged and never fails or cancels the job.
    private async Task RunHeartbeatAsync(JobMessage message, DateTimeOffset token, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(timings.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    using var tickScope = scopeFactory.CreateScope();
                    tickScope.ServiceProvider.GetRequiredService<JobHouseholdContext>().HouseholdId = message.HouseholdId;
                    var lifecycle = tickScope.ServiceProvider.GetRequiredService<IBackgroundJobLifecycle>();
                    if (!await lifecycle.TryHeartbeatAsync(message.JobId, token, cancellationToken))
                    {
                        // Ownership lost (failed as stale, or the row was deleted). Stop heartbeating but
                        // do not cancel the running use case: it may be mid-transaction.
                        logger.LogWarning("Background job {JobId} no longer owns its row; stopping its heartbeat.", message.JobId);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Heartbeat for background job {JobId} failed; will retry on the next tick.", message.JobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal end: the loop was stopped by StopHeartbeatAsync or by shutdown.
        }
    }

    private static async Task StopHeartbeatAsync(CancellationTokenSource heartbeatCts, Task heartbeat)
    {
        await heartbeatCts.CancelAsync();
        await heartbeat;
    }
}
