using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Regression test for the production incident where a large Smart Plug import's per-row
// conflict-tolerant insert loop (SmartPlugImportRepository.AddAsync) ran far longer than
// ReceiveMessagesAsync's default 30-second visibility timeout, so the same still-processing
// message became visible again and was picked up and reprocessed concurrently — BackgroundJobProcessor
// treats an already-Processing job as "reuse it", not skip — compounding without bound
// (~2.7M log lines in 16 minutes before the household's Log Analytics workspace hit its daily cap).
public class AzureStorageQueueJobProcessingServiceTests
{
    [Fact]
    public async Task Polls_with_a_visibility_timeout_generous_enough_to_outlast_a_slow_large_import()
    {
        var queueClient = Substitute.For<QueueClient>();
        queueClient.CreateIfNotExistsAsync(Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns((Response?)null);
        var pollStarted = new TaskCompletionSource();
        queueClient.ReceiveMessagesAsync(Arg.Any<int?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                pollStarted.TrySetResult();
                return Response.FromValue(Array.Empty<QueueMessage>(), Substitute.For<Response>());
            });
        // processor is never invoked on this path (ReceiveMessagesAsync returns zero messages), so
        // a real BackgroundJobProcessor (which needs a working DI scope/DbContext) isn't needed.
        var service = new AzureStorageQueueJobProcessingService(queueClient, null!, NullLogger<AzureStorageQueueJobProcessingService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // The default (an omitted/null visibilityTimeout) is 30 seconds per the Azure Storage
        // Queues SDK — far shorter than a large Eve Home import's per-row insert loop can run,
        // which is exactly what caused the redelivery storm. Assert it's set, and generously long.
        await queueClient.Received().ReceiveMessagesAsync(
            Arg.Any<int?>(), Arg.Is<TimeSpan?>(t => t.HasValue && t.Value >= TimeSpan.FromMinutes(30)), Arg.Any<CancellationToken>());
    }
}
