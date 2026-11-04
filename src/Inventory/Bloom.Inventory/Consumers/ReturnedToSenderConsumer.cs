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

        var units = new List<(Guid SkuId, string? Serial, int Quantity, string Outcome)>();
        foreach (var u in message.Units)
        {
            if (u.Outcome is not (Sealed or Failed))
                throw new PermanentFailureException($"Returned-to-sender {refId}: unknown outcome '{u.Outcome}'.");
            if (!items.TryGetValue(u.SkuId, out var item))
                throw new PermanentFailureException($"Returned-to-sender {refId} names SKU {u.SkuId}, which has no stock record.");
            if (item.Serialized)
            {
                var serial = u.Serial is null ? null : Identifiers.NormalizeSerial(u.Serial);
                units.Add((u.SkuId, serial ?? throw new PermanentFailureException($"Returned-to-sender {refId}: serialized SKU {u.SkuId} needs a valid serial."), 1, u.Outcome));
            }
            else if (u.Quantity > 0) units.Add((u.SkuId, null, u.Quantity, u.Outcome));
            else throw new PermanentFailureException($"Returned-to-sender {refId}: SKU {u.SkuId} has a non-positive quantity.");
        }

        var wanted = units.Where(u => u.Serial is not null).Select(u => u.Serial!).ToList();
        var found = await db.SerialUnits.AsNoTracking().Where(s => wanted.Contains(s.Serial)).ToDictionaryAsync(s => s.Serial, ct);
        foreach (var u in units.Where(u => u.Serial is not null))
            if (!found.TryGetValue(u.Serial!, out var s) || s.SkuId != u.SkuId || s.Status != SerialStatuses.Shipped || s.RefId != refId)
                throw new PermanentFailureException($"Returned-to-sender {refId}: serial {u.Serial} is not a shipped unit of SKU {u.SkuId} for this shipment.");
        if (wanted.Distinct().Count() != wanted.Count)
            throw new PermanentFailureException($"Returned-to-sender {refId} repeats a serial.");

        // The ledger's ref_type follows the allocation kind of refId (plan R11); an order id is the fallback.
        var kind = await db.StockAllocations.AsNoTracking().Where(a => a.RefId == refId).Select(a => a.Kind).FirstOrDefaultAsync(ct);
        var refType = kind == AllocationKinds.Reship ? RefTypes.Reship : RefTypes.Order;
        var now = clock.GetUtcNow();

        foreach (var u in units.Where(u => u.Serial is not null))
            if (u.Outcome == Sealed)
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE serial_units SET status = {SerialStatuses.InStock}, ref_id = NULL, version = version + 1, updated_at = {now}
                    WHERE serial = {u.Serial} AND status = {SerialStatuses.Shipped}
                    """, ct);
            else
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE serial_units SET status = {SerialStatuses.Damaged}, version = version + 1, updated_at = {now}
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
