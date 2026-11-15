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

    private IChannel? _channel;
    private bool _channelFailed; // set when a pass lost its channel; the loop then pauses 1 s (R7)

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var claimed = await RunOnceAsync(ct);
                    if (_channelFailed)
                    {
                        _channelFailed = false;
                        await Task.Delay(TimeSpan.FromSeconds(1), ct); // R7
                    }
                    else if (claimed < BatchSize) await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Outbox relay iteration failed");
                    await DisposeChannelAsync();
                    _channelFailed = false; // this catch already pauses
                    await Task.Delay(TimeSpan.FromSeconds(1), ct); // R7
                }
            }
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

        // One confirm deadline for the whole pass: it bounds how long the FOR UPDATE locks are held (§7.2).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RabbitMqConnection.ConfirmTimeout);

        foreach (var row in rows)
        {
            if (deadline.IsCancellationRequested) break; // the rest are untouched and tried next pass
            // A channel closed by the broker (404 on a bad exchange, ...) is replaced before the next row.
            if (_channel is { IsOpen: false }) await DisposeChannelAsync();
            try
            {
                if (_channel is null)
                {
                    try { _channel = await connection.CreateConfirmChannelAsync(deadline.Token); }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // Every following row would fail the same way, so the pass ends here.
                        failed.Add((row, $"Channel re-open failed: {(ex is OperationCanceledException ? "timed out" : ex.Message)}"));
                        await DisposeChannelAsync();
                        break;
                    }
                }
                await PublishAsync(_channel, row, deadline.Token);
                sent.Add(row.Id);
            }
            catch (PublishException ex) // nack: the channel stays usable
            {
                failed.Add((row, ex.Message));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Only the row in flight is unconfirmed (§7.2 step 5); the rest are untouched and tried next.
                var error = ex is OperationCanceledException ? $"No publisher confirm within {RabbitMqConnection.ConfirmTimeout.TotalSeconds:0} s" : ex.Message;
                failed.Add((row, error));
                await DisposeChannelAsync();
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

        if (sent.Count > 0) metrics.OutboxPublish("confirmed", sent.Count);
        if (failed.Count > 0) metrics.OutboxPublish("failed", failed.Count);
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

        // occurredAt is the row's database insert time, not the writer's clock (00-reliability §5.2, §10).
        var body = (Envelope.Parse(Encoding.UTF8.GetBytes(row.Payload)) with { OccurredAt = row.CreatedAt }).ToUtf8Bytes();
        await channel.BasicPublishAsync(row.Exchange, row.RoutingKey, mandatory: false, properties, body, ct);
    }

    private static Dictionary<string, object?> LogScope(OutboxMessage row) => new()
    {
        ["message_id"] = row.Id,
        ["message_type"] = row.MessageType,
        ["correlation_id"] = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers)?.GetValueOrDefault("correlation_id"),
    };

    private async Task DisposeChannelAsync()
    {
        _channelFailed = true;
        var channel = _channel;
        _channel = null;
        if (channel is null) return;
        try { await channel.DisposeAsync(); }
        catch (Exception ex) { logger.LogDebug(ex, "Disposing outbox relay channel failed"); }
    }
}
