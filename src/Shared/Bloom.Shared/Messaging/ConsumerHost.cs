using System.Diagnostics;
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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(catalog.Consumers.Select(c => SuperviseAsync(c, ct)));
    }

    private async Task SuperviseAsync(ConsumerDefinition def, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await RunSessionAsync(def, ct))
                {
                    await WaitForDatabaseAsync(def, ct);
                    continue; // the probe already waited, resume at once
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Consumer {Consumer} channel failed", def.Queue);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct); // R7
        }
    }

    /// <summary>
    /// One channel's lifetime: returns when the channel shuts down, a delivery could not be settled, or the host stops.
    /// Returns true when it ended because the service's own database is unreachable (HLD-ADR-019).
    /// </summary>
    private async Task<bool> RunSessionAsync(ConsumerDefinition def, CancellationToken ct)
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
        var outage = false;
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            if (!await OnDeliveryAsync(def, channel, delivery, session)) return;
            outage = true; // before Cancel, so RunSessionAsync sees it when the session ends
            session.Cancel();
        };
        await channel.BasicConsumeAsync(def.Queue, autoAck: false, consumer, ct);
        await closed.Task;
        return outage;
    }

    /// <summary>Probes the service's own database with <c>SELECT 1</c> every 1 s until it answers (HLD-ADR-019).</summary>
    private async Task WaitForDatabaseAsync(ConsumerDefinition def, CancellationToken ct)
    {
        logger.LogWarning("Consumer {Consumer} paused: its database is unreachable", def.Queue);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (await DatabaseReachableAsync(ct)) return;
        }
    }

    /// <summary>One <c>SELECT 1</c> on the service's own database, bounded to 5 s so a black-holed server can't stall the caller.</summary>
    private async Task<bool> DatabaseReachableAsync(CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DbContext>().Database.ExecuteSqlRawAsync("SELECT 1", bounded.Token);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Database probe failed");
            return false;
        }
    }

    /// <summary>Returns true when the delivery was requeued because the database is unreachable; the caller then ends the session.</summary>
    private async Task<bool> OnDeliveryAsync(ConsumerDefinition def, IChannel channel, BasicDeliverEventArgs delivery, CancellationTokenSource session)
    {
        if (session.IsCancellationRequested) return false;
        try
        {
            return await ProcessAsync(def, channel, delivery, session.Token);
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
        return false;
    }

    /// <summary>Returns true when the delivery was NACKed with requeue because the service's own database is unreachable.</summary>
    private async Task<bool> ProcessAsync(ConsumerDefinition def, IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
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
            return false;
        }
        if (!def.Bindings.Any(b => b.MessageType == envelope.MessageType))
        {
            await DeadLetterAsync(def, channel, delivery, $"unknown message type: {envelope.MessageType}", ct);
            return false;
        }

        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["message_id"] = envelope.MessageId,
            ["message_type"] = envelope.MessageType,
            ["correlation_id"] = envelope.CorrelationId,
            ["consumer"] = def.Queue,
        });
        ActivityContext.TryParse(RabbitMqConnection.HeaderString(headers, "traceparent"), null, out var parent);
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
                MessageScope.Current.Value = envelope;
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
            MessageScope.Current.Value = null;
            if (tx is not null) await tx.DisposeAsync();
        }

        if (failure is null)
        {
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            metrics.ConsumerMessage(def.Queue, result);
            if (result == "success") metrics.ConsumerHandle(def.Queue, Stopwatch.GetElapsedTime(started).TotalSeconds);
            return false;
        }

        // The classifier is a cheap pre-filter: a client-side command timeout on a live database looks the same,
        // so confirm with the probe. Reachable means a normal failure and keeps the 5-attempt rule.
        if (DatabaseOutage.IsOutage(failure) && !await DatabaseReachableAsync(ct))
        {
            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: true, ct); // HLD-ADR-019: no attempt counted
            return true;
        }

        logger.LogWarning(failure, "Handler failed on attempt {Attempt}", attempt);
        if (failure is PermanentFailureException || attempt >= MaxAttempts)
        {
            await DeadLetterAsync(def, channel, delivery, $"{failure.GetType().Name}: {failure.Message}", ct);
            return false;
        }
        await RabbitMqConnection.RepublishAsync(channel, MessageNames.RetryQueue(def.Queue, MessageNames.RetryDelays[attempt - 1].Delay), delivery,
            h => h["x-attempt"] = attempt + 1, ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        metrics.ConsumerMessage(def.Queue, "retry");
        return false;
    }

    private async Task DeadLetterAsync(ConsumerDefinition def, IChannel channel, BasicDeliverEventArgs delivery, string reason, CancellationToken ct)
    {
        logger.LogWarning("Dead-lettering a message from {Consumer}: {Reason}", def.Queue, reason);
        await RabbitMqConnection.RepublishAsync(channel, MessageNames.DeadLetterQueue(def.Queue), delivery, h =>
        {
            h["x-failure-reason"] = reason;
            h["x-failed-at"] = DateTime.UtcNow.ToString("O");
        }, ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
        metrics.ConsumerMessage(def.Queue, "dlq");
    }
}
