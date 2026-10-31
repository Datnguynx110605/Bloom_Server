using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Shared.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using StackExchange.Redis;

namespace Bloom.Gateway;

/// <summary>
/// Applies <c>identity.tokens-revoked.v1</c> to Redis (LLD 07 §6.3). One channel, prefetch 1, ACK only after the write.
/// On a Redis error it holds the delivery and retries every second; it never NACKs. On a lost connection it reconnects
/// after 1 s and declares the topology again (plan P17).
/// </summary>
public sealed class RevocationConsumer(IConfiguration config, RevocationWriter writer, ILogger<RevocationConsumer> logger) : BackgroundService
{
    internal const string Queue = "api-gateway.tokens-revoked";
    private const string MessageType = "identity.tokens-revoked.v1";
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stop);
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                logger.LogWarning(e, "The revocation consumer failed");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!stop.IsCancellationRequested)
                logger.LogWarning("The revocation consumer lost its connection; reconnecting");
            await Task.Delay(Retry, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>Consumes until the channel closes or the worker stops.</summary>
    private async Task ConsumeAsync(CancellationToken stop)
    {
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(stop); // also cancelled when the channel shuts down
        await using var connection = await new ConnectionFactory
        {
            Uri = new Uri(config["Gateway:RabbitMq"]!),
            AutomaticRecoveryEnabled = false, // the loop in ExecuteAsync reconnects and declares the topology again
            ClientProvidedName = "gateway-worker",
        }.CreateConnectionAsync(stop);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stop);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            closed.Cancel();
            return Task.CompletedTask;
        };

        var exchange = MessageNames.EventExchange("identity");
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, cancellationToken: stop);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: stop);
        await channel.QueueBindAsync(Queue, exchange, MessageType, cancellationToken: stop);
        await channel.BasicQosAsync(0, 1, false, stop);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, closed.Token);
        await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stop);
        await Task.Delay(Timeout.Infinite, closed.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        try
        {
            if (Parse(delivery.Body) is { } message)
                await WriteHoldingAsync(message, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        }
        catch (Exception e) when (e is OperationCanceledException or AlreadyClosedException)
        {
            // Stopping, or the channel is gone: no ACK, so the broker redelivers the message.
        }
    }

    /// <summary>The payload, or null (after an error log without payload values) when the message can never be applied.</summary>
    private TokensRevokedV1? Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            var envelope = Envelope.Parse(body.Span);
            if (envelope.MessageType == MessageType)
                return envelope.GetPayload<TokensRevokedV1>();
            logger.LogError("Skipped message {MessageId}: unexpected message type {MessageType}", envelope.MessageId, envelope.MessageType);
        }
        catch (Exception e) when (e is JsonException or PermanentFailureException)
        {
            logger.LogError("Skipped a message that is not a valid {MessageType} ({Error})", MessageType, e.GetType().Name);
        }
        return null;
    }

    /// <summary>Owner decision: keep the delivery unacked and retry every second until the write succeeds (a NACK loop would hit the quorum queue's delivery limit).</summary>
    private async Task WriteHoldingAsync(TokensRevokedV1 message, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await writer.WriteAsync(message);
                return;
            }
            catch (Exception e) when (e is RedisException or TimeoutException)
            {
                logger.LogWarning("Redis write failed; holding the delivery and retrying ({Error})", e.GetType().Name);
                await Task.Delay(Retry, ct);
            }
        }
    }
}
