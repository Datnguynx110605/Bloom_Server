using Microsoft.EntityFrameworkCore;
using Bloom.Inventory.Data;

namespace Bloom.Inventory.Worker;

/// <summary>
/// Daily (plan R5, LLD-ADR-014): deletes <c>released_orders</c> rows older than 30 days, 1,000 rows per statement, until a statement deletes fewer.
/// Same batch delete as payment's nightly purge (the shared purger's rules are fixed).
/// </summary>
public sealed class TombstonePurger(IServiceScopeFactory scopes, TimeProvider time, ILogger<TombstonePurger> logger) : BackgroundService
{
    private const int Batch = 1000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Tombstone purge failed; retrying tomorrow");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        while (await db.Database.ExecuteSqlAsync($"""
            DELETE FROM released_orders WHERE order_id IN (SELECT order_id FROM released_orders WHERE released_at < now() - interval '30 days' LIMIT {Batch})
            """, ct) >= Batch) { }
    }
}
