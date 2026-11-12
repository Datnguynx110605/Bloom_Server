using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Consumers;

/// <summary>
/// <c>inventory.receive-customer-return.v1</c> (02-inventory §6.7, INV-R16, plan R17): an accepted unit is held, not sellable: serial <c>shipped</c> → <c>returned</c>
/// (one unit per serial) or <c>returned += quantity</c> for a non-serialized SKU. A serial that isn't <c>shipped</c> is real drift: permanent failure before any write.
/// Runs in <c>ConsumerHost</c>'s transaction (plan R10); SKU rows are locked first (plan R9).
/// </summary>
public sealed class CustomerReturnConsumer(InventoryDbContext db, StockLocks locks, Ledger ledger, TimeProvider clock) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Command(FulfillmentMessages.ReceiveCustomerReturn)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var message = envelope.GetPayload<ReceiveCustomerReturnV1>();
        var items = await locks.LockAsync(message.Units.Select(u => u.SkuId), ct);

        var units = await ReturnUnits.ResolveAsync(db, $"Customer return of order {message.OrderId}", items,
            message.Units.Select(u => (u.SkuId, u.Serial, u.Quantity)), null, ct);
        var wanted = units.Where(u => u.Serial is not null).Select(u => u.Serial!).ToList();

        var now = clock.GetUtcNow();
        foreach (var serial in wanted)
            await db.Database.ExecuteSqlAsync($"""
                UPDATE serial_units SET status = {SerialStatuses.Returned}, version = version + 1, updated_at = {now}
                WHERE serial = {serial} AND status = {SerialStatuses.Shipped}
                """, ct);
        foreach (var (skuId, q) in units.GroupBy(u => u.SkuId).OrderBy(g => g.Key).Select(g => (g.Key, g.Sum(u => u.Quantity))))
        {
            await db.Database.ExecuteSqlAsync($"UPDATE stock_items SET returned = returned + {q}, updated_at = {now} WHERE sku_id = {skuId}", ct);
            ledger.Add(skuId, EntryTypes.CustomerReturned, new(Returned: q), RefTypes.Order, message.OrderId);
        }
    }
}
