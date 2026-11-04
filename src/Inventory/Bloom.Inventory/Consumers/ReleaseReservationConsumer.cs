using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Consumers;

/// <summary>
/// <c>inventory.release-reservation.v1</c> (02-inventory §6.5, plan R17): frees every allocation of the order (reships included) and always replies
/// <c>reservation-released</c>, also when nothing needed releasing. Runs in <c>ConsumerHost</c>'s transaction (plan R10); the order's SKU rows are
/// locked in ascending order before any allocation changes status (plan R9), so the allocator below sees a stable queue.
/// </summary>
public sealed class ReleaseReservationConsumer(InventoryDbContext db, StockLocks locks, Allocator allocator, Ledger ledger, OutboxWriter outbox, TimeProvider clock) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Command(InventoryMessages.ReleaseReservation)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var orderId = envelope.GetPayload<ReleaseReservationV1>().OrderId;

        // Lock first, then read the statuses: a payment or shipment racing this release has finished by the time the lock is ours.
        // A reship may add a SKU between the read and the lock; lock the union and read again until the set is stable (advisory locks are re-entrant).
        var locked = new HashSet<Guid>();
        List<StockAllocation> allocations;
        while (true)
        {
            allocations = await db.StockAllocations.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.SkuId).ToListAsync(ct);
            if (allocations.All(a => locked.Contains(a.SkuId))) break;
            locked.UnionWith(allocations.Select(a => a.SkuId));
            await locks.LockAsync(locked, ct);
        }

        var now = clock.GetUtcNow();
        var freed = new SortedSet<Guid>();
        foreach (var a in allocations)
        {
            if (a.Status is not (AllocationStatuses.Reserved or AllocationStatuses.Backordered or AllocationStatuses.Committed)) continue; // shipped, released: no-op

            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE stock_allocations SET status = {AllocationStatuses.Released}, version = version + 1, updated_at = {now}
                WHERE ref_id = {a.RefId} AND sku_id = {a.SkuId} AND status = {a.Status}
                """, ct) == 0) continue;

            var (q, refType) = (a.Quantity, a.Kind == AllocationKinds.Reship ? RefTypes.Reship : RefTypes.Order);
            switch (a.Status)
            {
                case AllocationStatuses.Reserved:
                    await UpdateCountersAsync($"""
                        UPDATE stock_items SET reserved = reserved - {q}, updated_at = {now} WHERE sku_id = {a.SkuId} AND reserved >= {q}
                        """, a, ct);
                    ledger.Add(a.SkuId, EntryTypes.ReservationReleased, new(Reserved: -q), refType, a.RefId);
                    break;
                case AllocationStatuses.Committed:
                    await UpdateCountersAsync($"""
                        UPDATE stock_items SET committed = committed - {q}, on_hand = on_hand + {q}, updated_at = {now} WHERE sku_id = {a.SkuId} AND committed >= {q}
                        """, a, ct);
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE serial_units SET status = {SerialStatuses.InStock}, ref_id = NULL, version = version + 1, updated_at = {now}
                        WHERE ref_id = {a.RefId} AND sku_id = {a.SkuId} AND status = {SerialStatuses.Assigned}
                        """, ct);
                    ledger.Add(a.SkuId, EntryTypes.CommitmentReleased, new(OnHand: q, Committed: -q), refType, a.RefId);
                    freed.Add(a.SkuId);
                    break;
            }
        }

        foreach (var skuId in freed) await allocator.RunAsync(skuId, ct); // freed units serve waiting pre-orders first
        outbox.PublishEvent(InventoryMessages.ReservationReleased, new ReservationReleasedV1(orderId));
    }

    private async Task UpdateCountersAsync(FormattableString update, StockAllocation a, CancellationToken ct)
    {
        if (await db.Database.ExecuteSqlAsync(update, ct) == 0)
            throw new InvalidOperationException($"Allocation {a.RefId}/{a.SkuId} ({a.Status}) is not backed by stock counters.");
    }
}
