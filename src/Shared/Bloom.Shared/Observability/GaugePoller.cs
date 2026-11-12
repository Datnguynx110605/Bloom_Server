using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bloom.Shared.Observability;

/// <summary>Feeds the backlog and DLQ gauges every 15 s (R9): one DB query, one passive declare per DLQ.</summary>
internal sealed class GaugePoller(
    IServiceScopeFactory scopes, RabbitMqConnection connection, ConsumerCatalog catalog, BloomMetrics metrics,
    ILogger<GaugePoller> logger) : BackgroundService
{
    private sealed class Backlog
    {
        public long Pending { get; set; }
        public double OldestAge { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await PollOnceAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogError(ex, "Gauge poll failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            var backlog = await scope.ServiceProvider.GetRequiredService<DbContext>().Database
                .SqlQuery<Backlog>($"""
                    SELECT count(*) AS "Pending",
                           coalesce(extract(epoch FROM now() - min(created_at)), 0)::float8 AS "OldestAge"
                    FROM outbox_messages WHERE status = 'pending'
                    """)
                .SingleAsync(ct);
            metrics.ReportOutboxBacklog(backlog.Pending, backlog.OldestAge);
        }

        foreach (var queue in catalog.DeadLetterQueues)
            metrics.ReportDlqDepth(queue, await connection.QueueDepthAsync(queue, ct));
    }
}
