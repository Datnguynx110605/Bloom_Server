using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bloom.Shared.Observability;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Bloom.Shared.Messaging;

/// <summary>
/// Publishes pending <c>outbox_messages</c> rows with publisher confirms (LLD 00-reliability §7.2).
/// Several instances are safe: <c>FOR UPDATE SKIP LOCKED</c> gives each a disjoint batch.
/// </summary>
internal sealed class OutboxRelay(
    IServiceScopeFactory scopes, RabbitMqConnection connection, BloomMetrics metrics, ILogger<OutboxRelay> logger)
    : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (await RunOnceAsync(ct) < BatchSize) await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Outbox relay iteration failed");
                    await DisposeChannelAsync();
                    await Task.Delay(TimeSpan.FromSeconds(1), ct); // R7
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await DisposeChannelAsync();
        }
    }

    /// <summary>One pass of §7.2. Returns the number of rows claimed.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.Set<OutboxMessage>()
            .FromSql($"SELECT * FROM outbox_messages WHERE status = 'pending' ORDER BY id LIMIT {BatchSize} FOR UPDATE SKIP LOCKED")
            .AsNoTracking()
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        var sent = new List<Guid>();
        var failed = new List<(OutboxMessage Row, string Error)>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConfirmTimeout);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            try
            {
                _channel ??= await connection.CreateConfirmChannelAsync(timeout.Token);
                await PublishAsync(_channel, row, timeout.Token);
                sent.Add(row.Id);
            }
            catch (PublishException ex) // nack: the channel stays usable
            {
                failed.Add((row, ex.Message));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Confirm timeout or channel/connection error: this row and the rest are unconfirmed.
                var error = ex is OperationCanceledException ? $"No publisher confirm within {ConfirmTimeout.TotalSeconds:0} s" : ex.Message;
                failed.AddRange(rows.Skip(i).Select(r => (r, error)));
                await DisposeChannelAsync();
                break;
            }
        }

        if (sent.Count > 0)
            await db.Database.ExecuteSqlAsync($"UPDATE outbox_messages SET status = 'sent', sent_at = now() WHERE id = ANY({sent.ToArray()})", ct);
        foreach (var (row, error) in failed)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE outbox_messages SET attempts = attempts + 1, last_error = {error} WHERE id = {row.Id}", ct);
            using (logger.BeginScope(LogScope(row)))
                logger.LogWarning("Outbox publish failed: {Error}", error);
        }
        await tx.CommitAsync(ct);

        foreach (var _ in sent) metrics.OutboxPublish("confirmed");
        foreach (var _ in failed) metrics.OutboxPublish("failed");
        return rows.Count;
    }

    private static async Task PublishAsync(IChannel channel, OutboxMessage row, CancellationToken ct)
    {
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers) ?? [];
        ActivityContext.TryParse(headers.GetValueOrDefault("traceparent"), null, out var parent);
        using var activity = BloomActivity.Source.StartActivity($"{row.Exchange} publish", ActivityKind.Producer, parent);

        var properties = new BasicProperties
        {
            MessageId = row.Id.ToString(),
            Type = row.MessageType,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
        };
        if ((activity?.Id ?? headers.GetValueOrDefault("traceparent")) is { } traceparent)
            properties.Headers = new Dictionary<string, object?> { ["traceparent"] = traceparent };

        await channel.BasicPublishAsync(row.Exchange, row.RoutingKey, mandatory: false, properties,
            Encoding.UTF8.GetBytes(row.Payload), ct);
    }

    private static Dictionary<string, object?> LogScope(OutboxMessage row) => new()
    {
        ["message_id"] = row.Id,
        ["message_type"] = row.MessageType,
        ["correlation_id"] = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers)?.GetValueOrDefault("correlation_id"),
    };

    private async Task DisposeChannelAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is null) return;
        try { await channel.DisposeAsync(); }
        catch (Exception ex) { logger.LogDebug(ex, "Disposing outbox relay channel failed"); }
    }
}
