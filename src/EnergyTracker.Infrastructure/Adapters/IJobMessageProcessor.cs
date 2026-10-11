namespace EnergyTracker.Infrastructure.Adapters;

// Seam between the Azure queue consumer and BackgroundJobProcessor, so the consumer's loop can be
// tested without a database. Returning normally means "delete the message"; throwing means "leave it".
internal interface IJobMessageProcessor
{
    Task ProcessAsync(JobMessage message, CancellationToken cancellationToken);

    Task FailRetriesExhaustedAsync(JobMessage message, CancellationToken cancellationToken);
}
