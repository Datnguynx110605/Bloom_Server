using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Consumers;

/// <summary>
/// <c>order.paid.v1</c> (02-inventory §6.2, plan R17). A regular order (<c>launchId</c> null) turns its <c>reserved</c> allocations into
/// <c>committed</c> ones; a pre-order (<c>launchId</c> set) queues one <c>backordered</c> allocation per SKU and runs the allocator (§6.4).
/// Runs in <c>ConsumerHost</c>'s transaction (plan R10); SKU rows are locked in ascending order before any allocation is touched (plan R9).
/// </summary>
public sealed class OrderPaidConsumer(InventoryDbContext db, StockLocks locks, Allocator allocator, Ledger ledger, TimeProvider clock) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("order", OrderMessages.Paid)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var paid = envelope.GetPayload<OrderPaidV1>();
        if (paid.LaunchId is null) await CommitReservedAsync(paid.OrderId, ct);
        else await BackorderAsync(paid, ct);
    }

    private async Task CommitReservedAsync(Guid orderId, CancellationToken ct)
    {
        var reserved = await db.StockAllocations.AsNoTracking()
            .Where(a => a.OrderId == orderId && a.Status == AllocationStatuses.Reserved).OrderBy(a => a.SkuId).ToListAsync(ct);
        await locks.LockAsync(reserved.Select(a => a.SkuId), ct);
        var now = clock.GetUtcNow();
        foreach (var a in reserved)
        {
            // Status guard after the lock: a duplicate or a racing release already moved the allocation.
            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE stock_allocations SET status = {AllocationStatuses.Committed}, version = version + 1, updated_at = {now}
                WHERE ref_id = {a.RefId} AND sku_id = {a.SkuId} AND status = {AllocationStatuses.Reserved}
                """, ct) == 0) continue;

            var q = a.Quantity;
            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE stock_items SET reserved = reserved - {q}, on_hand = on_hand - {q}, committed = committed + {q}, updated_at = {now}
                WHERE sku_id = {a.SkuId} AND reserved >= {q}
                """, ct) == 0)
                throw new InvalidOperationException($"Reservation {a.RefId}/{a.SkuId} is not backed by reserved stock.");
            ledger.Add(a.SkuId, EntryTypes.Committed, new(OnHand: -q, Reserved: -q, Committed: q), RefTypes.Order, a.RefId);
        }
    }

    private async Task BackorderAsync(OrderPaidV1 paid, CancellationToken ct)
    {
        var lines = paid.Lines.GroupBy(l => l.SkuId).Select(g => (SkuId: g.Key, Quantity: g.Sum(l => l.Quantity))).OrderBy(l => l.SkuId).ToList();
        if (lines.Count == 0 || lines.Any(l => l.SkuId == Guid.Empty || l.Quantity <= 0))
            throw new PermanentFailureException($"Pre-order {paid.OrderId} has no lines or a line with a non-positive quantity.");

        await locks.LockAsync(lines.Select(l => l.SkuId), ct);
        var now = clock.GetUtcNow();
        foreach (var (skuId, q) in lines)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO stock_allocations (ref_id, sku_id, order_id, kind, quantity, status, paid_at, version, created_at, updated_at)
                VALUES ({paid.OrderId}, {skuId}, {paid.OrderId}, {AllocationKinds.Preorder}, {q}, {AllocationStatuses.Backordered}, {paid.PaidAt}, 0, {now}, {now})
                ON CONFLICT (ref_id, sku_id) DO NOTHING
                """, ct);
        foreach (var (skuId, _) in lines) await allocator.RunAsync(skuId, ct);
    }
}
