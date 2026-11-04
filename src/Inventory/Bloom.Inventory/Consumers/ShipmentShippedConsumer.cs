using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Consumers;

/// <summary>
/// <c>fulfillment.shipment-shipped.v1</c> (02-inventory §6.7, plan R17): the <c>committed</c> allocations of <c>refId</c> become <c>shipped</c>, their assigned
/// serials too. Works on inventory's own allocations and serials; the message's <c>serials[]</c> change nothing. A serialized allocation with fewer assigned
/// serials than its quantity is a permanent failure before any write (I-12). Runs in <c>ConsumerHost</c>'s transaction (plan R10); SKU rows are locked first (plan R9).
/// </summary>
public sealed class ShipmentShippedConsumer(InventoryDbContext db, StockLocks locks, Ledger ledger, TimeProvider clock) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("fulfillment", FulfillmentMessages.ShipmentShipped)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var refId = envelope.GetPayload<ShipmentShippedV1>().RefId;

        // Lock first, then read: a release racing this shipment has finished by the time the lock is ours.
        var skus = await db.StockAllocations.AsNoTracking().Where(a => a.RefId == refId).Select(a => a.SkuId).ToListAsync(ct);
        var items = await locks.LockAsync(skus, ct);
        var committed = await db.StockAllocations.AsNoTracking()
            .Where(a => a.RefId == refId && a.Status == AllocationStatuses.Committed).OrderBy(a => a.SkuId).ToListAsync(ct);

        var assigned = await db.SerialUnits.AsNoTracking().Where(s => s.RefId == refId && s.Status == SerialStatuses.Assigned)
            .GroupBy(s => s.SkuId).Select(g => new { SkuId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.SkuId, g => g.Count, ct);
        foreach (var a in committed)
            if (items[a.SkuId].Serialized && assigned.GetValueOrDefault(a.SkuId) < a.Quantity)
                throw new PermanentFailureException($"Shipment {refId} ships {a.Quantity} of serialized SKU {a.SkuId} with {assigned.GetValueOrDefault(a.SkuId)} serials assigned.");

        var now = clock.GetUtcNow();
        foreach (var a in committed)
        {
            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE stock_allocations SET status = {AllocationStatuses.Shipped}, version = version + 1, updated_at = {now}
                WHERE ref_id = {a.RefId} AND sku_id = {a.SkuId} AND status = {AllocationStatuses.Committed}
                """, ct) == 0) continue;

            var q = a.Quantity;
            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE stock_items SET committed = committed - {q}, updated_at = {now} WHERE sku_id = {a.SkuId} AND committed >= {q}
                """, ct) == 0)
                throw new InvalidOperationException($"Allocation {a.RefId}/{a.SkuId} is not backed by committed stock.");
            await db.Database.ExecuteSqlAsync($"""
                UPDATE serial_units SET status = {SerialStatuses.Shipped}, version = version + 1, updated_at = {now}
                WHERE ref_id = {a.RefId} AND sku_id = {a.SkuId} AND status = {SerialStatuses.Assigned}
                """, ct);
            ledger.Add(a.SkuId, EntryTypes.Shipped, new(Committed: -q), a.Kind == AllocationKinds.Reship ? RefTypes.Reship : RefTypes.Order, a.RefId);
        }
    }
}
