using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using EnergyTracker.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace EnergyTracker.Infrastructure.Tests;

// Story 11.3 AC #7: without a handler, a body that fails Base64 decoding makes the whole
// ReceiveMessagesAsync call throw and the message cycles forever. The SDK does not delete the message
// itself, so the handler must.
public class QueueMessageDecodingFailureHandlerTests
{
    private readonly QueueClient _queue = Substitute.For<QueueClient>();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    private static QueueMessage Received(string body = "%%% not base64 %%%") =>
        QueuesModelFactory.QueueMessage("msg-7", "receipt-7", body, 1, null, null, null);

    private QueueMessageDecodingFailedEventArgs Args(QueueMessage? received, PeekedMessage? peeked, bool sync) =>
        new(_queue, received, peeked, sync, TestContext.Current.CancellationToken);

    private static int ErrorLogs(ILogger logger) =>
        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log) && (LogLevel)c.GetArguments()[0]! == LogLevel.Error);

    [Fact]
    public async Task A_received_message_that_fails_to_decode_is_deleted_asynchronously()
    {
        _queue.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<Response>());

        await QueueMessageDecodingFailureHandler.HandleAsync(Args(Received(), null, sync: false), _logger);

        await _queue.Received(1).DeleteMessageAsync("msg-7", "receipt-7", Arg.Any<CancellationToken>());
        ErrorLogs(_logger).ShouldBe(1);
    }

    [Fact]
    public async Task A_received_message_is_deleted_synchronously_when_the_receive_call_itself_is_synchronous()
    {
        await QueueMessageDecodingFailureHandler.HandleAsync(Args(Received(), null, sync: true), _logger);

        _queue.Received(1).DeleteMessage("msg-7", "receipt-7", Arg.Any<CancellationToken>());
        await _queue.DidNotReceive().DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_logged_error_names_the_message_id_but_never_carries_the_foreign_body()
    {
        const string body = "SECRET-FOREIGN-PAYLOAD";
        _queue.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<Response>());

        await QueueMessageDecodingFailureHandler.HandleAsync(Args(Received(body), null, sync: false), _logger);

        var logged = string.Join('\n', _logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(c => c.GetArguments()[2]!.ToString()));
        logged.ShouldContain("msg-7");
        logged.ShouldNotContain(body);
    }

    [Fact]
    public async Task A_failure_to_delete_is_logged_and_swallowed_so_it_never_throws_into_the_receive_call()
    {
        _queue.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new RequestFailedException("storage is down"));

        await Should.NotThrowAsync(() => QueueMessageDecodingFailureHandler.HandleAsync(Args(Received(), null, sync: false), _logger));

        ErrorLogs(_logger).ShouldBe(2);
    }

    [Fact]
    public async Task A_delete_cancelled_by_shutdown_is_not_logged_as_an_error()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _queue.DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var args = new QueueMessageDecodingFailedEventArgs(_queue, Received(), null, false, cts.Token);

        await Should.NotThrowAsync(() => QueueMessageDecodingFailureHandler.HandleAsync(args, _logger));

        ErrorLogs(_logger).ShouldBe(1);
    }

    [Fact]
    public async Task A_peeked_message_is_only_logged_because_it_has_no_pop_receipt_to_delete_with()
    {
        var peeked = QueuesModelFactory.PeekedMessage("msg-9", "body", 1, null, null);

        await QueueMessageDecodingFailureHandler.HandleAsync(Args(null, peeked, sync: false), _logger);

        await _queue.DidNotReceive().DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        ErrorLogs(_logger).ShouldBe(1);
    }
}
