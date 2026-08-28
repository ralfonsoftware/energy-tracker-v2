namespace EnergyTracker.Application.Ports;

// TPayload must be a plain JSON-serializable record — never a delegate/closure (AD-6): a
// delegate can't cross AzureStorageQueueJobQueue's serialize-to-a-cloud-queue boundary the way it
// silently "works" against InProcessChannelJobQueue's in-memory channel.
// QueuedByHouseholdMemberId/OriginalFileName (Story 3.6/AD-6 extension) are optional so this
// record stays usable for any future non-file job type — captured at enqueue time, before a
// SmartPlugImport row exists, so a Waiting/Processing row in the job history list still has
// something to render.
public record JobEnvelope<TPayload>(
    Guid JobId, Guid HouseholdId, string JobType, TPayload Payload,
    Guid? QueuedByHouseholdMemberId = null, string? OriginalFileName = null);

// One port, two adapters (InProcessChannelJobQueue, AzureStorageQueueJobQueue), config-selected
// once at the composition root (AD-6). Enqueuing only hands the envelope off — the caller learns
// completion by polling GET /api/jobs/{id}, never a callback/event from this port.
public interface IBackgroundJobQueue
{
    Task EnqueueAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken cancellationToken);
}
