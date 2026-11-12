using System.Threading.Channels;
using Bloom.Shared.Errors;
using Bloom.Shared.Messaging;
using Grpc.Core;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Bloom.Shared.DeadLetters;

internal enum PassAction { Keep, Replay, Discard }

/// <summary>
/// One pass over a DLQ (00-reliability §7.7): takes the message count at the start and consumes exactly that many
/// messages with an exclusive consumer, so late arrivals stay. Quorum queues ignore the exclusive flag, so callers
/// serialise passes themselves (<see cref="DeadLetterAdminService"/> holds a PostgreSQL advisory lock). Never nacks or requeues.
/// </summary>
internal class DeadLetterPass(RabbitMqConnection connection)
{
    /// <summary>
    /// Longest wait for the next delivery. With prefetch 1 the next one follows the previous ACK within milliseconds, so
    /// 10 s only elapses when the snapshot count is higher than what is left (queue purged or a message removed meanwhile).
    /// </summary>
    public TimeSpan DeliveryTimeout { get; init; } = TimeSpan.FromSeconds(10);
    private static readonly string[] ReplayStrips = ["x-attempt", "x-failure-reason", "x-failed-at"];

    /// <summary>Per message: <paramref name="decide"/>, publish (replay, or keep at the DLQ tail), <paramref name="audit"/> (replay and discard only), ACK.</summary>
    /// <exception cref="RpcException"><c>DLQ_NOT_FOUND</c> when the queue is not declared; <c>DLQ_BUSY</c> when another operation holds the queue.</exception>
    public virtual async Task RunAsync(
        string dlq, Func<BasicDeliverEventArgs, PassAction> decide, Func<BasicDeliverEventArgs, PassAction, Task> audit, CancellationToken ct)
    {
        await using var channel = await connection.CreateConfirmChannelAsync(ct);
        uint count;
        try
        {
            count = (await channel.QueueDeclarePassiveAsync(dlq, ct)).MessageCount;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            throw BloomErrors.Create(StatusCode.NotFound, BloomErrors.DlqNotFound, $"Queue '{dlq}' does not exist.");
        }

        await channel.BasicQosAsync(0, 1, false, ct);
        var deliveries = Channel.CreateUnbounded<BasicDeliverEventArgs>();
        var consumer = new AsyncEventingBasicConsumer(channel);
        // The broker's buffers are reused once the handler returns, so keep a copy.
        consumer.ReceivedAsync += (_, d) => deliveries.Writer.WriteAsync(Copy(d), ct).AsTask();
        // A channel that closes mid-pass (broker restart, dropped connection) ends the wait for deliveries at once.
        channel.ChannelShutdownAsync += (_, e) =>
        {
            deliveries.Writer.TryComplete(new IOException($"The channel to '{dlq}' closed during the pass: {e.ReplyText}"));
            return Task.CompletedTask;
        };
        string tag;
        try
        {
            tag = await channel.BasicConsumeAsync(dlq, false, "", false, true, null, consumer, ct); // exclusive, even for an empty DLQ
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 403)
        {
            throw Busy(dlq);
        }

        if (count == 0)
        {
            await channel.BasicCancelAsync(tag, cancellationToken: ct);
            return;
        }

        for (var i = 1u; i <= count; i++)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(DeliveryTimeout);
            BasicDeliverEventArgs delivery;
            try
            {
                delivery = await deliveries.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // No error reason in §8 fits; surfaces as UNKNOWN. Unacked deliveries go back when the channel is disposed (no nack).
                throw new TimeoutException($"'{dlq}' held {i - 1} of the {count} messages counted at the start; no more arrived within {DeliveryTimeout.TotalSeconds:0} s.");
            }
            var action = decide(delivery);
            switch (action)
            {
                case PassAction.Replay:
                    await RabbitMqConnection.RepublishAsync(channel, dlq[..^".dlq".Length], delivery, h => { foreach (var name in ReplayStrips) h.Remove(name); }, ct);
                    break;
                case PassAction.Keep: await RabbitMqConnection.RepublishAsync(channel, dlq, delivery, _ => { }, ct); break;
            }
            if (action != PassAction.Keep) await audit(delivery, action);
            if (i == count) await channel.BasicCancelAsync(tag, cancellationToken: ct); // before the last ACK, or the broker delivers a late arrival
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
    }

    internal static RpcException Busy(string dlq) =>
        BloomErrors.Create(StatusCode.Aborted, BloomErrors.DlqBusy, $"Another operation is running on '{dlq}'.");

    private static BasicDeliverEventArgs Copy(BasicDeliverEventArgs d) =>
        new(d.ConsumerTag, d.DeliveryTag, d.Redelivered, d.Exchange, d.RoutingKey,
            new BasicProperties(d.BasicProperties) { Headers = d.BasicProperties.Headers is { } h ? new Dictionary<string, object?>(h) : null },
            d.Body.ToArray(), d.CancellationToken);
}
