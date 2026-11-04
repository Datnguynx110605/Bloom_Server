using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Consumers;

/// <summary>
/// <c>inventory.receive-returned-to-sender.v1</c> (02-inventory §6.7, INV-R20, plan R17): <c>sealed</c> units go back to stock (<c>on_hand += q</c>, then the allocator
/// runs, §6.4); <c>failed</c> units are <c>damaged</c>. A serial that isn't <c>shipped</c> for <c>refId</c>, or an unknown outcome, is permanent failure before any write.
/// Runs in <c>ConsumerHost</c>'s transaction (plan R10); SKU rows are locked first (plan R9).
/// </summary>
public sealed class ReturnedToSenderConsumer(InventoryDbContext db, StockLocks locks, Allocator allocator, Ledger ledger, TimeProvider clock) : IMessageHandler
{
    private const string Sealed = "sealed";
    private const string Failed = "failed";

    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Command(FulfillmentMessages.ReceiveReturnedToSender)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var message = envelope.GetPayload<ReceiveReturnedToSenderV1>();
        var refId = message.RefId;
        var items = await locks.LockAsync(message.Units.Select(u => u.SkuId), ct);

        foreach (var u in message.Units)
            if (u.Outcome is not (Sealed or Failed))
                throw new PermanentFailureException($"Returned-to-sender {refId}: unknown outcome '{u.Outcome}'.");
        var resolved = await ReturnUnits.ResolveAsync(db, $"Returned-to-sender {refId}", items,
            message.Units.Select(u => (u.SkuId, u.Serial, u.Quantity)), refId, ct);
        var units = resolved.Zip(message.Units, (r, u) => (r.SkuId, r.Serial, r.Quantity, u.Outcome)).ToList();

        // The ledger's ref_type follows the allocation kind of refId (plan R11); an order id is the fallback.
        var kind = await db.StockAllocations.AsNoTracking().Where(a => a.RefId == refId).Select(a => a.Kind).FirstOrDefaultAsync(ct);
        var refType = RefTypes.Of(kind);
        var now = clock.GetUtcNow();

        foreach (var u in units.Where(u => u.Serial is not null)) // a damaged unit keeps its ref_id, already checked against refId
            await db.Database.ExecuteSqlAsync($"""
                UPDATE serial_units SET status = {(u.Outcome == Sealed ? SerialStatuses.InStock : SerialStatuses.Damaged)}, ref_id = {(u.Outcome == Sealed ? (Guid?)null : refId)}, version = version + 1, updated_at = {now}
                WHERE serial = {u.Serial} AND status = {SerialStatuses.Shipped}
                """, ct);

        foreach (var g in units.GroupBy(u => (u.SkuId, u.Outcome)).OrderBy(g => g.Key.SkuId))
        {
            var (skuId, outcome) = g.Key;
            var q = g.Sum(u => u.Quantity);
            if (outcome == Sealed)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE stock_items SET on_hand = on_hand + {q}, updated_at = {now} WHERE sku_id = {skuId}", ct);
                ledger.Add(skuId, EntryTypes.RtsRestocked, new(OnHand: q), refType, refId);
            }
            else
            {
                await db.Database.ExecuteSqlAsync($"UPDATE stock_items SET damaged = damaged + {q}, updated_at = {now} WHERE sku_id = {skuId}", ct);
                ledger.Add(skuId, EntryTypes.RtsDamaged, new(Damaged: q), refType, refId);
            }
        }

        foreach (var skuId in units.Where(u => u.Outcome == Sealed).Select(u => u.SkuId).Distinct().Order()) await allocator.RunAsync(skuId, ct);
    }
}
