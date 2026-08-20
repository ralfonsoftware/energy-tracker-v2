using System.Text.Json;
using EnergyTracker.Application;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// Shared dequeue-side processing loop body for both queue adapters' hosted BackgroundServices —
// inserts the BackgroundJob row as processing starts (DB-persisted, not in-memory: Container Apps
// can scale to zero/multiple replicas between enqueue and a client's next poll), resolves the
// job's use case by JobType, and updates status/ErrorMessage/CompletedAtUtc on finish.
public class BackgroundJobProcessor(IServiceScopeFactory scopeFactory, ILogger<BackgroundJobProcessor> logger)
{
    internal async Task ProcessAsync(JobMessage message, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        // AD-3's job-processing resolution path: set before anything downstream resolves
        // EnergyTrackerDbContext, so the very first query in this scope already sees the right
        // Household via the standard query filter.
        services.GetRequiredService<JobHouseholdContext>().HouseholdId = message.HouseholdId;

        var dbContext = services.GetRequiredService<EnergyTrackerDbContext>();

        var job = new BackgroundJob
        {
            Id = message.JobId,
            HouseholdId = message.HouseholdId,
            JobType = message.JobType,
            Status = BackgroundJobStatus.Processing,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        dbContext.BackgroundJobs.Add(job);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Idempotency guard against redelivery (e.g. Azure Storage Queue's visibility timeout
            // expiring mid-processing on a slow file): a row for this JobId already exists, so our
            // insert hit the primary key. Optimistic (try-insert, reconcile on conflict) rather
            // than check-then-act, so the common case — no redelivery — costs one round trip, not
            // two, and there's no TOCTOU gap between the check and the insert. A row already in a
            // terminal state means an earlier delivery already finished — skip, or the message
            // would never get deleted and retry forever with no dead-letter path. A row still
            // Processing is either a live concurrent redelivery or an orphan from a crashed
            // instance — reuse it (UPDATE, not INSERT) so a genuinely stuck job still gets retried.
            dbContext.Entry(job).State = EntityState.Detached;
            var existingJob = await dbContext.BackgroundJobs.SingleAsync(j => j.Id == message.JobId, cancellationToken);
            if (existingJob.Status != BackgroundJobStatus.Processing)
            {
                logger.LogWarning(
                    "Background job {JobId} already recorded as {Status}; skipping duplicate delivery.", message.JobId, existingJob.Status);
                return;
            }

            job = existingJob;
        }

        try
        {
            switch (message.JobType)
            {
                case JobTypes.ProcessSmartPlugImport:
                    var payload = JsonSerializer.Deserialize<ProcessSmartPlugImportPayload>(message.PayloadJson)
                        ?? throw new InvalidOperationException($"Job {message.JobId}: payload deserialized to null.");
                    var useCase = services.GetRequiredService<ProcessSmartPlugImport>();
                    await useCase.ExecuteAsync(message.HouseholdId, message.JobId, payload, cancellationToken);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown JobType '{message.JobType}'.");
            }

            job.Status = BackgroundJobStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            // Shutdown/redeploy, not a real processing failure — leave the job Processing (never
            // Failed) so a redelivered/re-dequeued message is still treated as retryable rather
            // than permanently discarded.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background job {JobId} ({JobType}) failed", message.JobId, message.JobType);
            job.Status = BackgroundJobStatus.Failed;
            // SmartPlugImportValidationException's message is deliberately user-facing (bad file
            // content/name) — anything else is an unexpected internal failure whose raw .Message
            // (file paths, DB errors, etc.) must never be forwarded verbatim to the client that
            // polls GET /api/jobs/{id}.
            job.ErrorMessage = ex is SmartPlugImportValidationException
                ? ex.Message
                : "An unexpected error occurred while processing this import.";
        }

        job.CompletedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
