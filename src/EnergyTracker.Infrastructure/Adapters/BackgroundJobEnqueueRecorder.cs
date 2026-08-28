using EnergyTracker.Application.Ports;
using EnergyTracker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EnergyTracker.Infrastructure.Adapters;

// Shared enqueue-time BackgroundJob row insert (Story 3.6/AD-6 extension) — both
// InProcessChannelJobQueue and AzureStorageQueueJobQueue are registered AddSingleton, so DB
// access needs a fresh scope per call, same IServiceScopeFactory pattern BackgroundJobProcessor
// already uses. Factored into one class rather than duplicated in each adapter's EnqueueAsync.
public class BackgroundJobEnqueueRecorder(IServiceScopeFactory scopeFactory)
{
    public async Task RecordAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken cancellationToken)
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
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
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
