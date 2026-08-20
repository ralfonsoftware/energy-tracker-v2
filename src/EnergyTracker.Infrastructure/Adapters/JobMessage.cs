namespace EnergyTracker.Infrastructure.Adapters;

// The wire format both queue adapters agree on internally — JobEnvelope&lt;TPayload&gt;'s Payload
// serialized to JSON up front (at EnqueueAsync time) rather than carried as a live .NET object,
// so InProcessChannelJobQueue and AzureStorageQueueJobQueue behave identically regardless of
// which one a given deployment is configured to use.
internal record JobMessage(Guid JobId, Guid HouseholdId, string JobType, string PayloadJson);
