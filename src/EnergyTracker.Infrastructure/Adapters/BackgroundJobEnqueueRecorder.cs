using EnergyTracker.Application;
using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EnergyTracker.Infrastructure.Adapters;

// Shared enqueue-time BackgroundJob row insert (Story 3.6/AD-6 extension) — both
// InProcessChannelJobQueue and AzureStorageQueueJobQueue are registered AddSingleton, so DB
// access needs a fresh scope per call, same IServiceScopeFactory pattern BackgroundJobProcessor
// already uses. Factored into one class rather than duplicated in each adapter's EnqueueAsync.
public class BackgroundJobEnqueueRecorder(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
{
    public Task RecordAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken cancellationToken) =>
        RecordAsync(envelope, retryWhenSlotIsFree: true, cancellationToken);

    private async Task RecordAsync<TPayload>(
        JobEnvelope<TPayload> envelope, bool retryWhenSlotIsFree, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();

        dbContext.BackgroundJobs.Add(new BackgroundJob
        {
            Id = envelope.JobId,
            HouseholdId = envelope.HouseholdId,
            JobType = envelope.JobType,
            Status = BackgroundJobStatus.Queued,
            OriginalFileName = envelope.OriginalFileName,
            QueuedByHouseholdMemberId = envelope.QueuedByHouseholdMemberId,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (JobTypes.ExclusivePerHousehold.Contains(envelope.JobType))
        {
            // Story 11.14 (audit C10, AD-6): for a guarded type the filtered unique index rejected the row
            // because another is active. Probing for that row, instead of reading provider error codes,
            // is portable across both providers and yields the id the cleanup endpoint answers with. The
            // row is written before the queue send, so nothing was sent.
            var activeJobId = await FindActiveJobIdAsync(envelope.HouseholdId, envelope.JobType, cancellationToken);
            if (activeJobId is { } id)
            {
                throw new ActiveJobAlreadyExistsException(envelope.JobType, id);
            }

            if (retryWhenSlotIsFree)
            {
                // The winner reached Completed or Failed between the rejected insert and the probe (a
                // cleanup can finish in milliseconds), so the slot is free now: insert once more. A second
                // rejection is probed again, then propagates.
                await RecordAsync(envelope, retryWhenSlotIsFree: false, cancellationToken);
                return;
            }

            throw;
        }
    }

    // A fresh scope: the failed context above still tracks the rejected row. The explicit HouseholdId
    // predicate keeps the probe correct whichever Household the ambient scope resolves (AD-3: no
    // IgnoreQueryFilters; the global filter still applies on top).
    private async Task<Guid?> FindActiveJobIdAsync(Guid householdId, string jobType, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        return await dbContext.BackgroundJobs
            .AsNoTracking()
            .Where(j => j.HouseholdId == householdId && j.JobType == jobType
                && (j.Status == BackgroundJobStatus.Queued || j.Status == BackgroundJobStatus.Processing))
            .OrderByDescending(j => j.CreatedAtUtc)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Review-round-2 patch: compensating action for a queue adapter whose send fails *after*
    // RecordAsync above already committed the Queued row — without this, a transient send failure
    // (network blip, throttling) leaves a permanent phantom "Waiting" row, since Waiting/
    // Processing/Needs Mapping rows are exempt from the 30-day sweep.
    public async Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EnergyTrackerDbContext>();
        await dbContext.BackgroundJobs.Where(j => j.Id == jobId).ExecuteDeleteAsync(cancellationToken);
    }
}
