using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Consumers;

/// <summary>
/// <c>inventory.mark-lost-in-transit.v1</c> (02-inventory §6.7, plan R17): the units of the <c>shipped</c> allocations of <c>refId</c> become <c>lost</c>
/// (serials <c>shipped</c> → <c>lost</c>, <c>lost += count</c>; <c>lost += quantity</c> for a non-serialized SKU). The allocations stay <c>shipped</c>.
/// No shipped allocation, or a serialized one with no shipped serial that no earlier command counted, is real drift: permanent failure before any write.
/// A repeat for a ref whose serialized units are already counted is a no-op.
/// Runs in <c>ConsumerHost</c>'s transaction (plan R10); SKU rows are locked first (plan R9).
/// </summary>
public sealed class LostInTransitConsumer(InventoryDbContext db, StockLocks locks, Ledger ledger) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Command(FulfillmentMessages.MarkLostInTransit)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var refId = envelope.GetPayload<MarkLostInTransitV1>().RefId;

        var skus = await db.StockAllocations.AsNoTracking().Where(a => a.RefId == refId).Select(a => a.SkuId).ToListAsync(ct);
        var items = await locks.LockAsync(skus, ct);
        var shipped = await db.StockAllocations.AsNoTracking()
            .Where(a => a.RefId == refId && a.Status == AllocationStatuses.Shipped).OrderBy(a => a.SkuId).ToListAsync(ct);
        if (shipped.Count == 0) throw new PermanentFailureException($"Lost in transit {refId}: no shipped allocation.");

        var serials = await db.SerialUnits.AsNoTracking().Where(s => s.RefId == refId && s.Status == SerialStatuses.Shipped)
            .GroupBy(s => s.SkuId).Select(g => new { SkuId = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.SkuId, g => g.Count, ct);
        // The ledger holds what an earlier command of this refId already counted; a plain SKU's units are lost once (IN-7).
        var counted = await db.StockLedger.AsNoTracking().Where(e => e.RefId == refId && e.EntryType == EntryTypes.LostInTransit)
            .GroupBy(e => e.SkuId).Select(g => new { SkuId = g.Key, Lost = g.Sum(e => e.DLost) }).ToDictionaryAsync(g => g.SkuId, g => g.Lost, ct);
        var counts = new List<(StockAllocation Allocation, int Lost)>();
        foreach (var a in shipped)
        {
            if (!items[a.SkuId].Serialized)
            {
                var remaining = a.Quantity - counted.GetValueOrDefault(a.SkuId);
                if (remaining > 0) counts.Add((a, remaining));
            }
            else if (serials.TryGetValue(a.SkuId, out var n)) counts.Add((a, n));
            else if (counted.ContainsKey(a.SkuId)) continue; // its serials are already lost: a repeat is a no-op (02-inventory §8)
            else throw new PermanentFailureException($"Lost in transit {refId}: serialized SKU {a.SkuId} has no shipped serial.");
        }

        var now = await db.Database.NowAsync(ct);
        foreach (var (a, lost) in counts)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE serial_units SET status = {SerialStatuses.Lost}, version = version + 1, updated_at = {now}
                WHERE ref_id = {refId} AND sku_id = {a.SkuId} AND status = {SerialStatuses.Shipped}
                """, ct);
            await db.Database.ExecuteSqlAsync($"UPDATE stock_items SET lost = lost + {lost}, updated_at = {now} WHERE sku_id = {a.SkuId}", ct);
            ledger.Add(a.SkuId, EntryTypes.LostInTransit, new(Lost: lost), RefTypes.Of(a.Kind), refId, now);
        }
    }
}
