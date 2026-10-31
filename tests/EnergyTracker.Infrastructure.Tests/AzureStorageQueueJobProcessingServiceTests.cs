using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Regression test for the production incident where a large Smart Plug import's per-row
// conflict-tolerant insert loop (SmartPlugImportRepository.AddAsync) ran far longer than
// ReceiveMessagesAsync's default 30-second visibility timeout, so the same still-processing
// message became visible again and was picked up and reprocessed concurrently — BackgroundJobProcessor
// treats an already-Processing job as "reuse it", not skip — compounding without bound
// (~2.7M log lines in 16 minutes before the household's Log Analytics workspace hit its daily cap).
//
// Story 11.3 / AD-6 (amended 2026-10-10, audit C7): the consumer loop never faults the host on a
// storage error, receives one message at a time, fails a job after the dequeue limit, and deletes a
// message that can never be parsed.
public class AzureStorageQueueJobProcessingServiceTests
{
    // Millisecond timings so a backoff is observed through the logged delay, never by waiting it out.
    private static readonly QueueConsumerTimings FastTimings = new(
        TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80));

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private sealed class Harness
    {
        private readonly ConcurrentQueue<Func<Task<Response<QueueMessage[]>>>> _receives = new();
        private int _receiveCalls;

        public QueueClient Queue { get; } = Substitute.For<QueueClient>();

        public IJobMessageProcessor Processor { get; } = Substitute.For<IJobMessageProcessor>();

        public ILogger<AzureStorageQueueJobProcessingService> Logger { get; } = Substitute.For<ILogger<AzureStorageQueueJobProcessingService>>();

        public int ReceiveCalls => Volatile.Read(ref _receiveCalls);

        public Harness()
        {
            Queue.CreateIfNotExistsAsync(Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
                .Returns((Response?)null);
            Queue.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Substitute.For<Response>());
            Queue.ReceiveMessagesAsync(Arg.Any<int?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Interlocked.Increment(ref _receiveCalls);
                    return _receives.TryDequeue(out var next) ? next() : Task.FromResult(Messages());
                });
        }

        public void ThenReceive(params QueueMessage[] messages) => _receives.Enqueue(() => Task.FromResult(Messages(messages)));

        public void ThenReceiveFails() =>
            _receives.Enqueue(() => Task.FromException<Response<QueueMessage[]>>(new RequestFailedException("storage is down")));

        public AzureStorageQueueJobProcessingService CreateService(QueueConsumerTimings? timings = null) =>
            new(Queue, Processor, timings ?? FastTimings, Logger);

        private static Response<QueueMessage[]> Messages(params QueueMessage[] messages) =>
            Response.FromValue(messages, Substitute.For<Response>());
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message, IReadOnlyDictionary<string, object?> Properties);

    private static List<LogEntry> LogEntries(ILogger logger) =>
        logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(c =>
            {
                var args = c.GetArguments();
                var properties = (args[2] as IEnumerable<KeyValuePair<string, object?>> ?? [])
                    .Where(p => p.Key != "{OriginalFormat}")
                    .ToDictionary(p => p.Key, p => p.Value);
                return new LogEntry((LogLevel)args[0]!, (Exception?)args[3], args[2]!.ToString()!, properties);
            })
            .ToList();

    private static List<LogEntry> Errors(ILogger logger) => LogEntries(logger).Where(e => e.Level == LogLevel.Error).ToList();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within 10 seconds.");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static readonly Guid HouseholdId = Guid.NewGuid();

    private static QueueMessage NewMessage(long dequeueCount = 1, string? messageText = null, string messageId = "msg-1", Guid? jobId = null)
    {
        var job = new JobMessage(jobId ?? Guid.NewGuid(), HouseholdId, "CleanUpSmartPlugImportJobs", "{}");
        return QueuesModelFactory.QueueMessage(
            messageId, $"receipt-{messageId}", messageText ?? JsonSerializer.Serialize(job), dequeueCount, null, null, null);
    }

    private static JobMessage Parse(QueueMessage message) => JsonSerializer.Deserialize<JobMessage>(message.MessageText)!;

    private static bool IsFor(JobMessage message, QueueMessage queueMessage) => message == Parse(queueMessage);

    [Fact]
    public async Task Polls_with_a_visibility_timeout_generous_enough_to_outlast_a_slow_large_import()
    {
        var harness = new Harness();
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.ReceiveCalls > 0);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // The default (an omitted/null visibilityTimeout) is 30 seconds per the Azure Storage
        // Queues SDK — far shorter than a large Eve Home import's per-row insert loop can run,
        // which is exactly what caused the redelivery storm. Assert it's set, and generously long.
        await harness.Queue.Received().ReceiveMessagesAsync(
            Arg.Any<int?>(), Arg.Is<TimeSpan?>(t => t.HasValue && t.Value >= TimeSpan.FromMinutes(30)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Receives_exactly_one_message_per_call_because_they_are_processed_serially_under_one_visibility_timeout()
    {
        // Eight messages received under one 60-minute timeout were processed one after another, so
        // the later ones could become visible again (and be redelivered) before their turn came.
        var harness = new Harness();
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.ReceiveCalls > 0);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await harness.Queue.Received().ReceiveMessagesAsync(
            Arg.Is<int?>(n => n == 1), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceive().ReceiveMessagesAsync(
            Arg.Is<int?>(n => n != 1), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_CreateIfNotExists_is_logged_and_retried_without_faulting_the_service()
    {
        var harness = new Harness();
        var createCalls = 0;
        harness.Queue.CreateIfNotExistsAsync(Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref createCalls) == 1
                ? Task.FromException<Response>(new RequestFailedException("storage is down"))
                : Task.FromResult<Response>(Substitute.For<Response>()));
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.ReceiveCalls > 0);

        service.ExecuteTask!.IsCompleted.ShouldBeFalse();
        createCalls.ShouldBe(2);
        Errors(harness.Logger).ShouldHaveSingleItem().Exception.ShouldBeOfType<RequestFailedException>();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_queue_is_created_once_after_it_succeeds_not_on_every_poll()
    {
        var harness = new Harness();
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.ReceiveCalls >= 4);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await harness.Queue.Received(1).CreateIfNotExistsAsync(Arg.Any<IDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_repeatedly_failing_receive_keeps_the_service_running_and_backs_off_exponentially_up_to_the_cap()
    {
        var harness = new Harness();
        for (var i = 0; i < 6; i++)
        {
            harness.ThenReceiveFails();
        }

        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Errors(harness.Logger).Count >= 6);

        service.ExecuteTask!.IsCompleted.ShouldBeFalse();
        Errors(harness.Logger).Take(6).Select(e => (TimeSpan)e.Properties["Delay"]!).ShouldBe(
            [
                TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80),
                TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80),
            ]);
        Errors(harness.Logger).Take(6).Select(e => (int)e.Properties["FailureCount"]!).ShouldBe([1, 2, 3, 4, 5, 6]);
        await service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_receive_that_completes_resets_the_backoff_to_its_initial_delay()
    {
        var harness = new Harness();
        harness.ThenReceiveFails();
        harness.ThenReceiveFails();
        harness.ThenReceive();
        harness.ThenReceiveFails();
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Errors(harness.Logger).Count >= 3);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Errors(harness.Logger).Select(e => (TimeSpan)e.Properties["Delay"]!).ShouldBe(
            [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(20)]);
        Errors(harness.Logger).Last().Properties["FailureCount"].ShouldBe(1);
    }

    [Fact]
    public async Task A_message_that_fails_to_process_does_not_drive_the_backoff()
    {
        var harness = new Harness();
        harness.ThenReceive(NewMessage(messageId: "a"));
        harness.ThenReceive(NewMessage(messageId: "b"));
        harness.Processor.ProcessAsync(Arg.Any<JobMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("bad job"));
        var service = harness.CreateService(FastTimings with { InitialBackoff = OneHour });

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Errors(harness.Logger).Count >= 2);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Both messages were attempted back to back: a one-hour backoff would have blocked the second.
        await harness.Processor.Received(2).ProcessAsync(Arg.Any<JobMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_message_with_a_dequeue_count_over_five_is_failed_as_retries_exhausted_and_deleted_without_being_processed()
    {
        var harness = new Harness();
        var poison = NewMessage(dequeueCount: 6);
        harness.ThenReceive(poison);
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.Queue.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(QueueClient.DeleteMessageAsync)));
        await service.StopAsync(TestContext.Current.CancellationToken);

        await harness.Processor.DidNotReceive().ProcessAsync(Arg.Any<JobMessage>(), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            harness.Processor.FailRetriesExhaustedAsync(Arg.Is<JobMessage>(m => IsFor(m, poison)), Arg.Any<CancellationToken>());
            harness.Queue.DeleteMessageAsync(poison.MessageId, poison.PopReceipt, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task The_fifth_delivery_is_still_processed_normally()
    {
        var harness = new Harness();
        var fifth = NewMessage(dequeueCount: 5);
        harness.ThenReceive(fifth);
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.Queue.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(QueueClient.DeleteMessageAsync)));
        await service.StopAsync(TestContext.Current.CancellationToken);

        await harness.Processor.Received(1).ProcessAsync(Arg.Is<JobMessage>(m => IsFor(m, fifth)), Arg.Any<CancellationToken>());
        await harness.Processor.DidNotReceive().FailRetriesExhaustedAsync(Arg.Any<JobMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_retries_exhausted_failure_that_throws_leaves_the_message_undeleted_and_the_loop_running()
    {
        var harness = new Harness();
        var poison = NewMessage(dequeueCount: 6, messageId: "poison");
        var next = NewMessage(messageId: "next");
        harness.ThenReceive(poison);
        harness.ThenReceive(next);
        harness.Processor.FailRetriesExhaustedAsync(Arg.Any<JobMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is down"));
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.Queue.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(QueueClient.DeleteMessageAsync)));
        await service.StopAsync(TestContext.Current.CancellationToken);

        await harness.Queue.DidNotReceive().DeleteMessageAsync(poison.MessageId, poison.PopReceipt, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).DeleteMessageAsync(next.MessageId, next.PopReceipt, Arg.Any<CancellationToken>());
        Errors(harness.Logger).ShouldContain(e => e.Exception is InvalidOperationException);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("null")]
    [InlineData("{\"JobId\":\"not-a-guid\"}")]
    public async Task A_message_that_cannot_become_a_JobMessage_is_logged_and_deleted_immediately_without_processing(string body)
    {
        var harness = new Harness();
        var broken = NewMessage(dequeueCount: 1, messageText: body);
        harness.ThenReceive(broken);
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.Queue.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(QueueClient.DeleteMessageAsync)));
        await service.StopAsync(TestContext.Current.CancellationToken);

        await harness.Processor.DidNotReceive().ProcessAsync(Arg.Any<JobMessage>(), Arg.Any<CancellationToken>());
        await harness.Processor.DidNotReceive().FailRetriesExhaustedAsync(Arg.Any<JobMessage>(), Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).DeleteMessageAsync(broken.MessageId, broken.PopReceipt, Arg.Any<CancellationToken>());
        var error = Errors(harness.Logger).ShouldHaveSingleItem();
        error.Message.ShouldContain(broken.MessageId);
        error.Message.ShouldNotContain(body, Case.Sensitive);
    }

    [Fact]
    public async Task An_unexpected_processing_exception_leaves_the_message_undeleted_and_moves_on_to_the_next_one()
    {
        var harness = new Harness();
        var failing = NewMessage(dequeueCount: 2, messageId: "failing");
        var healthy = NewMessage(messageId: "healthy");
        harness.ThenReceive(failing);
        harness.ThenReceive(healthy);
        harness.Processor.ProcessAsync(Arg.Is<JobMessage>(m => IsFor(m, failing)), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.Queue.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(QueueClient.DeleteMessageAsync)));
        await service.StopAsync(TestContext.Current.CancellationToken);

        await harness.Queue.DidNotReceive().DeleteMessageAsync(failing.MessageId, failing.PopReceipt, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).DeleteMessageAsync(healthy.MessageId, healthy.PopReceipt, Arg.Any<CancellationToken>());
        var error = Errors(harness.Logger).ShouldHaveSingleItem();
        error.Exception.ShouldBeOfType<InvalidOperationException>();
        error.Message.ShouldContain("failing");
        error.Message.ShouldContain("dequeue count 2");
    }

    [Fact]
    public async Task A_processed_message_is_deleted_only_after_processing_returned()
    {
        var harness = new Harness();
        var message = NewMessage();
        harness.ThenReceive(message);
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => harness.Queue.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(QueueClient.DeleteMessageAsync)));
        await service.StopAsync(TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            harness.Processor.ProcessAsync(Arg.Is<JobMessage>(m => IsFor(m, message)), Arg.Any<CancellationToken>());
            harness.Queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, Arg.Any<CancellationToken>());
        });
        Errors(harness.Logger).ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopping_during_a_receive_completes_cleanly_without_logging_an_error()
    {
        var harness = new Harness();
        var receiving = new TaskCompletionSource();
        // The blocking receive observes the token the service passes in, as the real SDK call does.
        harness.Queue.ReceiveMessagesAsync(Arg.Any<int?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                receiving.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return Response.FromValue(Array.Empty<QueueMessage>(), Substitute.For<Response>());
            });
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await receiving.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
        Errors(harness.Logger).ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopping_during_a_backoff_delay_completes_promptly_and_logs_nothing_more()
    {
        var harness = new Harness();
        harness.ThenReceiveFails();
        var service = harness.CreateService(FastTimings with { InitialBackoff = OneHour, MaxBackoff = OneHour });

        await service.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Errors(harness.Logger).Count == 1);
        await service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
        Errors(harness.Logger).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Shutdown_cancelling_a_message_in_flight_leaves_it_undeleted_and_logs_no_error()
    {
        var harness = new Harness();
        var message = NewMessage();
        harness.ThenReceive(message);
        var processing = new TaskCompletionSource();
        harness.Processor.ProcessAsync(Arg.Any<JobMessage>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                processing.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            });
        var service = harness.CreateService();

        await service.StartAsync(TestContext.Current.CancellationToken);
        await processing.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await harness.Queue.DidNotReceive().DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Errors(harness.Logger).ShouldBeEmpty();
    }
}
