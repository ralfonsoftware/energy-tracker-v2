namespace EnergyTracker.Application.Ports;

// TPayload must be a plain JSON-serializable record — never a delegate/closure (AD-6): a
// delegate can't cross AzureStorageQueueJobQueue's serialize-to-a-cloud-queue boundary the way it
// silently "works" against InProcessChannelJobQueue's in-memory channel.
public record JobEnvelope<TPayload>(Guid JobId, Guid HouseholdId, string JobType, TPayload Payload);

// One port, two adapters (InProcessChannelJobQueue, AzureStorageQueueJobQueue), config-selected
// once at the composition root (AD-6). Enqueuing only hands the envelope off — the caller learns
// completion by polling GET /api/jobs/{id}, never a callback/event from this port.
public interface IBackgroundJobQueue
{
    Task EnqueueAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken cancellationToken);
}
