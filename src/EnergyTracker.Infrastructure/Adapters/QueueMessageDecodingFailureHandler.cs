using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;

namespace EnergyTracker.Infrastructure.Adapters;

// AD-6 (audit C7): QueueClientOptions.MessageDecodingFailed. Our own adapter always writes valid Base64,
// so a body that fails to decode comes from a foreign producer or is corrupt and can never succeed. The
// SDK does not delete such a message itself, and without a handler the whole ReceiveMessagesAsync call
// throws, so the same message would cycle forever. Must never throw into the receive call.
public static class QueueMessageDecodingFailureHandler
{
    public static async Task HandleAsync(QueueMessageDecodingFailedEventArgs args, ILogger logger)
    {
        // The body is deliberately not logged: it is arbitrary data from whoever wrote to the queue.
        if (args.ReceivedMessage is not { } received)
        {
            logger.LogError("A peeked queue message {MessageId} could not be decoded; it cannot be deleted from a peek.", args.PeekedMessage?.MessageId);
            return;
        }

        logger.LogError("Queue message {MessageId} could not be decoded; deleting it because it can never succeed.", received.MessageId);
        try
        {
            if (args.IsRunningSynchronously)
            {
                args.Queue.DeleteMessage(received.MessageId, received.PopReceipt, args.CancellationToken);
            }
            else
            {
                await args.Queue.DeleteMessageAsync(received.MessageId, received.PopReceipt, args.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a fault: the message simply stays in the queue.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not delete the undecodable queue message {MessageId}.", received.MessageId);
        }
    }
}
