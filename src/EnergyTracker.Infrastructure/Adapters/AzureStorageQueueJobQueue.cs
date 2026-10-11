using System.Text.Json;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using EnergyTracker.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// Cloud adapter (AD-6) — Storage Account + "jobs" queue are already provisioned by
// infra/modules/storage-queue.bicep; this adapter only reads the connection string handed to it
// via config. QueueClientOptions.MessageEncoding = Base64 (set where the QueueClient is
// constructed, Program.cs) so a JSON payload survives the queue message's XML envelope untouched.
public class AzureStorageQueueJobQueue(
    QueueClient queueClient, BackgroundJobEnqueueRecorder enqueueRecorder, ILogger<AzureStorageQueueJobQueue> logger) : IBackgroundJobQueue
{
    public async Task EnqueueAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken cancellationToken)
    {
        // Story 3.6/AD-6 extension: the BackgroundJob row (Queued) is persisted before the queue
        // send, so a job is visible in the household-wide job list the instant it's enqueued, not
        // only once dequeued.
        await enqueueRecorder.RecordAsync(envelope, cancellationToken);

        var message = new JobMessage(envelope.JobId, envelope.HouseholdId, envelope.JobType, JsonSerializer.Serialize(envelope.Payload));
        try
        {
            await queueClient.SendMessageAsync(JsonSerializer.Serialize(message), cancellationToken);
        }
        catch
        {
            // Review-round-2 patch: the Queued row above already committed by the time a transient
            // send failure (network blip, throttling) can surface — clean it up so this doesn't
            // become a permanent phantom "Waiting" row. CancellationToken.None, not the caller's
            // token: cancellation is exactly one reason the send above can fail, so this cleanup
            // must not itself be cancelled (same discipline as
            // SmartPlugImportRepository.DeletePartiallyPersistedImportAsync). Wrapped in its own
            // try/catch so a cleanup failure never masks the original send failure being rethrown.
            try
            {
                await enqueueRecorder.DeleteAsync(envelope.JobId, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(cleanupException,
                    "Best-effort cleanup of orphaned Queued BackgroundJob {JobId} failed after its queue send also failed; " +
                    "a phantom \"Waiting\" row may remain.", envelope.JobId);
            }

            throw;
        }
    }
}

// Paired hosted BackgroundService — polls the Storage Queue (no push-based API exists for it) one
// message at a time and hands it to the shared BackgroundJobProcessor. Why the loop looks like this
// (AD-6, audit C7): an exception escaping ExecuteAsync stops the whole host, so every storage or
// network error is caught, logged and retried with a capped backoff; a message is deleted only after
// a terminal decision (processed, dequeue limit reached, or unparseable); anything else stays in the
// queue and reappears after its visibility timeout.
public class AzureStorageQueueJobProcessingService : BackgroundService
{
    private readonly QueueClient queueClient;
    private readonly IJobMessageProcessor processor;
    private readonly QueueConsumerTimings timings;
    private readonly ILogger<AzureStorageQueueJobProcessingService> logger;

    // The public constructor is the one DI uses; the internal one takes the processor through its
    // interface seam so the loop can be tested without a database.
    public AzureStorageQueueJobProcessingService(
        QueueClient queueClient, BackgroundJobProcessor processor, QueueConsumerTimings timings,
        ILogger<AzureStorageQueueJobProcessingService> logger)
        : this(queueClient, (IJobMessageProcessor)processor, timings, logger)
    {
    }

    internal AzureStorageQueueJobProcessingService(
        QueueClient queueClient, IJobMessageProcessor processor, QueueConsumerTimings timings,
        ILogger<AzureStorageQueueJobProcessingService> logger)
    {
        this.queueClient = queueClient;
        this.processor = processor;
        this.timings = timings;
        this.logger = logger;
    }

    // ReceiveMessagesAsync's own visibilityTimeout parameter defaults to 30 seconds when omitted —
    // far shorter than a large Eve Home import's per-row conflict-tolerant insert loop
    // (SmartPlugImportRepository.AddAsync) can run. A still-processing message becoming visible
    // again gets redelivered to this same polling loop (BackgroundJobProcessor reuses an
    // already-Processing job rather than skipping it, by design, for the genuine crashed-instance
    // case), so the same import was reprocessed concurrently, compounding without bound — confirmed
    // live in production 2026-09-01 (~2.7M log lines in 16 minutes before the household's Log
    // Analytics workspace hit its daily ingestion cap). Set generously long, not tuned to any
    // measured worst case — the cost of a genuinely crashed instance's message staying invisible
    // this long is only a delayed retry, while too short reintroduces this exact bug.
    private static readonly TimeSpan MessageVisibilityTimeout = TimeSpan.FromMinutes(60);

    // A job is failed (job-retries-exhausted) on its sixth delivery. Reaching it takes at least five
    // visibility-timeout cycles, i.e. five hours of a message that keeps crashing or failing to delete.
    private const long MaxDequeueCount = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueEnsured = false;
        var backoff = timings.InitialBackoff;
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Inside the loop so a storage outage at startup is retried with backoff instead of
                // taking the host down before it ever served a request.
                if (!queueEnsured)
                {
                    await queueClient.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
                    queueEnsured = true;
                }

                // One message per call: they are processed serially under one visibility timeout, so the
                // later ones of a batch could become visible again before their turn came.
                var response = await queueClient.ReceiveMessagesAsync(
                    maxMessages: 1, visibilityTimeout: MessageVisibilityTimeout, cancellationToken: stoppingToken);
                backoff = timings.InitialBackoff;
                consecutiveFailures = 0;

                var messages = response.Value;
                if (messages.Length == 0)
                {
                    await Task.Delay(timings.PollInterval, stoppingToken);
                    continue;
                }

                foreach (var queueMessage in messages)
                {
                    await HandleMessageAsync(queueMessage, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    // Shutdown, not a fault: whatever was in flight stays undeleted for the next instance.
                    break;
                }

                consecutiveFailures++;
                logger.LogError(
                    ex, "Azure queue consumer failed (consecutive failures: {FailureCount}); retrying in {Delay}.",
                    consecutiveFailures, backoff);
                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, timings.MaxBackoff.Ticks));
            }
        }
    }

    // A failure here never drives the backoff: it is about one message, not about the queue.
    private async Task HandleMessageAsync(QueueMessage queueMessage, CancellationToken stoppingToken)
    {
        try
        {
            var message = ParseJobMessage(queueMessage);
            if (message is null)
            {
                // Can never succeed on a retry (foreign producer or corruption); don't wait for the dequeue limit.
                await queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, stoppingToken);
                return;
            }

            if (queueMessage.DequeueCount > MaxDequeueCount)
            {
                await processor.FailRetriesExhaustedAsync(message, stoppingToken);
            }
            else
            {
                await processor.ProcessAsync(message, stoppingToken);
            }

            // Only after a terminal decision: an exception above leaves the message to reappear.
            await queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(
                ex, "Unhandled error processing queue message {MessageId} (dequeue count {DequeueCount}); leaving it in the queue.",
                queueMessage.MessageId, queueMessage.DequeueCount);
        }
    }

    // The body is deliberately not logged: it is arbitrary data from whoever wrote to the queue.
    private JobMessage? ParseJobMessage(QueueMessage queueMessage)
    {
        try
        {
            var message = JsonSerializer.Deserialize<JobMessage>(queueMessage.MessageText);
            if (message is not null)
            {
                return message;
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Queue message {MessageId} is not a valid job message; deleting it.", queueMessage.MessageId);
            return null;
        }

        logger.LogError("Queue message {MessageId} deserialized to nothing; deleting it.", queueMessage.MessageId);
        return null;
    }
}
