using System.Text.Json;
using Azure.Storage.Queues;
using EnergyTracker.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// Cloud adapter (AD-6) — Storage Account + "jobs" queue are already provisioned by
// infra/modules/storage-queue.bicep; this adapter only reads the connection string handed to it
// via config. QueueClientOptions.MessageEncoding = Base64 (set where the QueueClient is
// constructed, Program.cs) so a JSON payload survives the queue message's XML envelope untouched.
public class AzureStorageQueueJobQueue(QueueClient queueClient, BackgroundJobEnqueueRecorder enqueueRecorder) : IBackgroundJobQueue
{
    public async Task EnqueueAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken cancellationToken)
    {
        // Story 3.6/AD-6 extension: the BackgroundJob row (Queued) is persisted before the queue
        // send, so a job is visible in the household-wide job list the instant it's enqueued, not
        // only once dequeued.
        await enqueueRecorder.RecordAsync(envelope, cancellationToken);

        var message = new JobMessage(envelope.JobId, envelope.HouseholdId, envelope.JobType, JsonSerializer.Serialize(envelope.Payload));
        await queueClient.SendMessageAsync(JsonSerializer.Serialize(message), cancellationToken);
    }
}

// Paired hosted BackgroundService — polls the Storage Queue (no push-based API exists for it)
// and hands each dequeued message to the shared BackgroundJobProcessor dispatch loop. A message
// is deleted only after processing runs (success or a caught use-case failure — both are terminal
// outcomes BackgroundJobProcessor already recorded on the BackgroundJob row); a message left
// undeleted because of an unexpected crash naturally reappears after its visibility timeout
// expires — no bespoke retry/backoff logic is built here.
public class AzureStorageQueueJobProcessingService(
    QueueClient queueClient, BackgroundJobProcessor processor, ILogger<AzureStorageQueueJobProcessingService> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queueClient.CreateIfNotExistsAsync(cancellationToken: stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var response = await queueClient.ReceiveMessagesAsync(maxMessages: 8, cancellationToken: stoppingToken);
            var messages = response.Value;

            if (messages.Length == 0)
            {
                await Task.Delay(PollInterval, stoppingToken);
                continue;
            }

            foreach (var queueMessage in messages)
            {
                try
                {
                    var message = JsonSerializer.Deserialize<JobMessage>(queueMessage.MessageText)
                        ?? throw new InvalidOperationException("Queue message deserialized to null.");
                    await processor.ProcessAsync(message, stoppingToken);
                    await queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Unhandled error processing queue message {MessageId}", queueMessage.MessageId);
                }
            }
        }
    }
}
