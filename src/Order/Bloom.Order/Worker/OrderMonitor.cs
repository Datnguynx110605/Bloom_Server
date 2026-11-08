using Bloom.Order.Data;
using Bloom.Order.Observability;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Worker;

/// <summary>
/// Every minute (03-order §10, plan R15): sets <c>orders_by_status</c>, <c>sagas_running{type}</c> and <c>sagas_stuck{type}</c> (open for more than 30 minutes), and logs one error
/// naming the count and the order ids when any saga is stuck. Reports only.
/// </summary>
public sealed class OrderMonitor(IServiceScopeFactory scopes, OrderMetrics metrics, TimeProvider clock, ILogger<OrderMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Order monitor run failed; retrying in a minute");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - StuckAfter;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        // Every status and type is reported, so one that has just emptied drops to 0 instead of keeping its last sample.
        var orders = OrderStatuses.All.ToDictionary(s => s, _ => 0L);
        foreach (var g in await db.Orders.AsNoTracking().GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct)) orders[g.Key] = g.Count;
        var open = await db.Set<OrderSaga>().AsNoTracking().Where(s => s.CompletedAt == null).Select(s => new { s.OrderId, s.Type, s.StartedAt }).ToListAsync(ct);
        var running = SagaTypes.All.ToDictionary(t => t, t => (long)open.Count(s => s.Type == t));
        var stuck = open.Where(s => s.StartedAt < cutoff).OrderBy(s => s.StartedAt).ToList();
        metrics.SetGauges(orders, running, SagaTypes.All.ToDictionary(t => t, t => (long)stuck.Count(s => s.Type == t)));

        if (stuck.Count > 0)
            logger.LogError("{Count} order sagas have been open for more than 30 minutes, orders: {Orders}", stuck.Count, string.Join(", ", stuck.Select(s => s.OrderId)));
    }
}
