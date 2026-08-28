using System.Text.Json;
using System.Threading.Channels;
using EnergyTracker.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// Default adapter (AD-6) — self-host and local dev, zero extra containers. Registered as a
// singleton so the same Channel instance backs both scoped API requests calling EnqueueAsync and
// the paired hosted BackgroundService reading it back off.
public class InProcessChannelJobQueue(BackgroundJobEnqueueRecorder enqueueRecorder) : IBackgroundJobQueue
{
    private readonly Channel<JobMessage> _channel = Channel.CreateUnbounded<JobMessage>();

    internal ChannelReader<JobMessage> Reader => _channel.Reader;

    public async Task EnqueueAsync<TPayload>(JobEnvelope<TPayload> envelope, CancellationToken cancellationToken)
    {
        // Story 3.6/AD-6 extension: the BackgroundJob row (Queued) is persisted before the
        // channel write, so a job is visible in the household-wide job list the instant it's
        // enqueued, not only once dequeued.
        await enqueueRecorder.RecordAsync(envelope, cancellationToken);

        var message = new JobMessage(envelope.JobId, envelope.HouseholdId, envelope.JobType, JsonSerializer.Serialize(envelope.Payload));
        await _channel.Writer.WriteAsync(message, cancellationToken);
    }
}

// Paired hosted BackgroundService — reads envelopes off InProcessChannelJobQueue's channel and
// hands each to the shared BackgroundJobProcessor dispatch loop.
public class InProcessChannelJobProcessingService(
    InProcessChannelJobQueue queue, BackgroundJobProcessor processor, ILogger<InProcessChannelJobProcessingService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await processor.ProcessAsync(message, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // BackgroundJobProcessor already catches and records use-case failures on the
                // BackgroundJob row itself — reaching here means something failed outside that
                // (e.g. the initial BackgroundJob insert). Log and keep the loop alive rather
                // than letting one bad message kill all future job processing.
                logger.LogError(ex, "Unhandled error processing background job {JobId}", message.JobId);
            }
        }
    }
}
