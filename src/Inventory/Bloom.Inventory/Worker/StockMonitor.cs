using Bloom.Inventory.Data;
using Bloom.Inventory.Observability;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Worker;

/// <summary>
/// Every minute: reports reservations older than 30 minutes (<c>stuck_reservations</c>), available stock and waiting backorders per SKU
/// (02-inventory §10, plan R18). Reports only: inventory never releases a reservation on its own (IR-05); order sends the release command.
/// </summary>
public sealed class StockMonitor(IServiceScopeFactory scopes, InventoryMetrics metrics, TimeProvider time, ILogger<StockMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Stock monitor run failed; retrying in a minute");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow() - StuckAfter;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var stuck = await db.StockAllocations.AsNoTracking().LongCountAsync(a => a.Status == AllocationStatuses.Reserved && a.CreatedAt < cutoff, ct);
        var available = await db.StockItems.AsNoTracking().ToDictionaryAsync(i => i.SkuId, i => i.OnHand - i.Reserved, ct);
        var backorders = await db.StockAllocations.AsNoTracking().Where(a => a.Status == AllocationStatuses.Backordered)
            .GroupBy(a => a.SkuId).Select(g => new { SkuId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.SkuId, g => g.Count, ct);
        metrics.SetStuck(stuck);
        metrics.SetStock(available, backorders);
    }
}
