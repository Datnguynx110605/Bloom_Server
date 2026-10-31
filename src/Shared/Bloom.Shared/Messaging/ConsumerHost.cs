using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bloom.Shared.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Bloom.Shared.Messaging;

/// <summary>
/// Runs every registered consumer queue with manual ACK, the inbox and the retry/DLQ routing of
/// LLD 00-reliability §7.3. One supervisor per queue re-opens its channel after any failure (R7).
/// </summary>
internal sealed class ConsumerHost(
    IServiceScopeFactory scopes, RabbitMqConnection connection, ConsumerCatalog catalog, BloomMetrics metrics,
    ILogger<ConsumerHost> logger)
    : BackgroundService
{
    private const ushort Prefetch = 16; // R9
    private const int MaxAttempts = 5;
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(catalog.Consumers.Select(c => SuperviseAsync(c, ct)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task SuperviseAsync(ConsumerDefinition def, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(def, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Consumer {Consumer} channel failed", def.Queue);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct); // R7
        }
    }

    /// <summary>One channel's lifetime: returns when the channel shuts down, a delivery could not be settled, or the host stops.</summary>
    private async Task RunSessionAsync(ConsumerDefinition def, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = session.Token.Register(() => closed.TrySetResult());

        await using var channel = await connection.CreateConfirmChannelAsync(ct);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            session.Cancel();
            return Task.CompletedTask;
        };
        await channel.BasicQosAsync(0, Prefetch, false, ct);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnDeliveryAsync(def, channel, delivery, session);
        await channel.BasicConsumeAsync(def.Queue, autoAck: false, consumer, ct);
        await closed.Task;
    }

    private async Task OnDeliveryAsync(ConsumerDefinition def, IChannel channel, BasicDeliverEventArgs delivery, CancellationTokenSource session)
    {
        if (session.IsCancellationRequested) return;
        try
        {
            await ProcessAsync(def, channel, delivery, session.Token);
        }
        catch (Exception ex) when (!session.IsCancellationRequested)
        {
            // A retry/DLQ publish or the ACK failed: leave the original unacked and drop the channel so the
            // broker redelivers it (R7, the §7.3 crash path). Closing happens in RunSessionAsync, off the dispatcher.
            logger.LogError(ex, "Consumer {Consumer} could not settle a delivery; closing its channel", def.Queue);
            session.Cancel();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessAsync(ConsumerDefinition def, IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var headers = delivery.BasicProperties.Headers;
        var attempt = headers is not null && headers.TryGetValue("x-attempt", out var raw) && raw is int n ? Math.Max(1, n) : 1;

        Envelope envelope;
        try
        {
            envelope = Envelope.Parse(delivery.Body.Span);
        }
        catch (JsonException ex)
        {
            await DeadLetterAsync(def, channel, delivery, $"invalid envelope: {ex.Message}", ct);
            return;
        }
        if (!def.Bindings.Any(b => b.MessageType == envelope.MessageType))
        {
            await DeadLetterAsync(def, channel, delivery, $"unknown message type: {envelope.MessageType}", ct);
            return;
        }

        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["message_id"] = envelope.MessageId,
            ["message_type"] = envelope.MessageType,
            ["correlation_id"] = envelope.CorrelationId,
            ["consumer"] = def.Queue,
        });
        ActivityContext.TryParse(HeaderString(headers, "traceparent"), null, out var parent);
        var started = Stopwatch.GetTimestamp();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        IDbContextTransaction? tx = null;
        var result = "success";
        Exception? failure = null;
        try
        {
            tx = await db.Database.BeginTransactionAsync(ct);
            var inserted = await db.Database.ExecuteSqlAsync(
                $"INSERT INTO inbox_messages (consumer, message_id, message_type) VALUES ({def.Queue}, {envelope.MessageId}, {envelope.MessageType}) ON CONFLICT DO NOTHING",
                ct);
            if (inserted == 0)
            {
                result = "duplicate";
            }
            else
            {
                MessageScope.Current = envelope;
                using var activity = BloomActivity.Source.StartActivity($"{def.Queue} process", ActivityKind.Consumer, parent);
                var handler = (IMessageHandler)scope.ServiceProvider.GetRequiredService(def.HandlerType);
                await handler.HandleAsync(envelope, ct);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failure = ex;
            if (tx is not null)
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch (Exception rollbackEx) { logger.LogDebug(rollbackEx, "Rollback failed"); }
            }
        }
        finally
        {
            MessageScope.Current = null;
            if (tx is not null) await tx.DisposeAsync();
        }

        if (failure is null)
        {
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            metrics.ConsumerMessage(def.Queue, result);
            if (result == "success") metrics.ConsumerHandle(def.Queue, Stopwatch.GetElapsedTime(started).TotalSeconds);
            return;
        }

        logger.LogWarning(failure, "Handler failed on attempt {Attempt}", attempt);
        if (failure is PermanentFailureException || attempt >= MaxAttempts)
        {
            await DeadLetterAsync(def, channel, delivery, $"{failure.GetType().Name}: {failure.Message}", ct);
            return;
        }
        await PublishAsync(channel, MessageNames.RetryQueue(def.Queue, MessageNames.RetryDelays[attempt - 1].Delay), delivery,
            h => h["x-attempt"] = attempt + 1, ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        metrics.ConsumerMessage(def.Queue, "retry");
    }

    private async Task DeadLetterAsync(ConsumerDefinition def, IChannel channel, BasicDeliverEventArgs delivery, string reason, CancellationToken ct)
    {
        logger.LogWarning("Dead-lettering a message from {Consumer}: {Reason}", def.Queue, reason);
        await PublishAsync(channel, MessageNames.DeadLetterQueue(def.Queue), delivery, h =>
        {
            h["x-failure-reason"] = reason;
            h["x-failed-at"] = DateTime.UtcNow.ToString("O");
        }, ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        metrics.ConsumerMessage(def.Queue, "dlq");
    }

    /// <summary>Republishes the delivery (body, properties, headers) through the default exchange and awaits the confirm.</summary>
    private static async Task PublishAsync(IChannel channel, string queue, BasicDeliverEventArgs delivery, Action<Dictionary<string, object?>> addHeaders, CancellationToken ct)
    {
        var headers = delivery.BasicProperties.Headers is { } h ? new Dictionary<string, object?>(h) : [];
        addHeaders(headers);
        var properties = new BasicProperties(delivery.BasicProperties) { Headers = headers, DeliveryMode = DeliveryModes.Persistent };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConfirmTimeout);
        await channel.BasicPublishAsync("", queue, mandatory: false, properties, delivery.Body, timeout.Token);
    }

    private static string? HeaderString(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var v)
            ? v switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string s => s, _ => null }
            : null;
}
