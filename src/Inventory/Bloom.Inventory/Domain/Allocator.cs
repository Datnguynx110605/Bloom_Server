using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Observability;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Domain;

/// <summary>Strict-FIFO backorder allocator (02-inventory §6.4, plan R12). The caller holds the SKU's <c>stock_items</c> lock, in its own transaction.</summary>
public sealed class Allocator(InventoryDbContext db, Ledger ledger, OutboxWriter outbox, InventoryMetrics metrics, TimeProvider clock)
{
    private const int OrderLockNamespace = 0x4F4C4B; // "OLK"
    private readonly HashSet<Guid> _announced = [];

    /// <summary>
    /// Runs the allocator for each SKU, then takes the order locks of every order it served once, in ascending order id, after the last SKU (IN-1): the callers
    /// hold all their SKU locks, so nothing holding an order lock waits on a SKU lock, and two transactions never take order locks in crossed order.
    /// </summary>
    public async Task RunAsync(IEnumerable<Guid> skuIds, CancellationToken ct)
    {
        var served = new HashSet<Guid>();
        foreach (var skuId in skuIds.Distinct()) served.UnionWith(await ServeAsync(skuId, ct));

        foreach (var orderId in served.Order())
        {
            // Another SKU's allocator may be committing this order's other lines right now; the order's lock makes the last one to commit see them all (§6.4 step 3).
            // Two-int form: a different key space from StockLocks' single-bigint SKU keys.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({OrderLockNamespace}, hashtext({orderId.ToString()}))", ct);
            var pending = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM stock_allocations WHERE order_id = {orderId} AND kind = {AllocationKinds.Preorder} AND status <> {AllocationStatuses.Committed}) AS "Value"
                """).SingleAsync(ct);
            if (!pending && _announced.Add(orderId)) outbox.PublishEvent(InventoryMessages.StockAllocated, new StockAllocatedV1(orderId));
        }
    }

    /// <summary>Commits the SKU's <c>backordered</c> allocations oldest first, stopping at the first one the free units can't cover. No <c>stock_items</c> row: the first guarded update changes nothing, so nothing happens. Returns the orders served.</summary>
    private async Task<List<Guid>> ServeAsync(Guid skuId, CancellationToken ct)
    {
        var waiting = await db.StockAllocations.AsNoTracking()
            .Where(a => a.SkuId == skuId && a.Status == AllocationStatuses.Backordered)
            .OrderBy(a => a.PaidAt).ThenBy(a => a.OrderId).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var served = new List<Guid>();
        foreach (var a in waiting)
        {
            var q = a.Quantity;
            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE stock_items SET on_hand = on_hand - {q}, committed = committed + {q}, updated_at = {now}
                WHERE sku_id = {skuId} AND on_hand - reserved >= {q}
                """, ct) == 0) break; // strict FIFO: nobody behind the oldest unfilled backorder is served, a line is never split

            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE stock_allocations SET status = {AllocationStatuses.Committed}, version = version + 1, updated_at = {now}
                WHERE ref_id = {a.RefId} AND sku_id = {skuId} AND status = {AllocationStatuses.Backordered}
                """, ct) == 0)
                throw new InvalidOperationException($"Allocation {a.RefId}/{skuId} left 'backordered' while the SKU row was locked.");
            ledger.Add(skuId, EntryTypes.BackorderAllocated, new(OnHand: -q, Committed: q), RefTypes.Of(a.Kind), a.RefId);
            var waited = (now - a.PaidAt!.Value).TotalSeconds;
            db.AfterCommit(() => metrics.BackorderAllocated(waited));
            served.Add(a.OrderId);
        }
        return served;
    }
}
