using Bloom.Inventory.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Domain;

/// <summary>The checks both return consumers share (plan R17). Every failure is permanent and happens before the caller writes anything.</summary>
public static class ReturnUnits
{
    /// <summary>
    /// Resolves each unit to a stock row and, for a serialized SKU, a normalized serial (quantity 1); then requires every serial to be a <c>shipped</c> unit of its SKU
    /// (for <paramref name="shipmentRefId"/> when given) and no serial to repeat. Returns the units in message order.
    /// </summary>
    public static async Task<List<(Guid SkuId, string? Serial, int Quantity)>> ResolveAsync(
        InventoryDbContext db, string label, IReadOnlyDictionary<Guid, StockItem> items,
        IEnumerable<(Guid SkuId, string? Serial, int Quantity)> raw, Guid? shipmentRefId, CancellationToken ct)
    {
        var units = new List<(Guid SkuId, string? Serial, int Quantity)>();
        foreach (var u in raw)
        {
            if (!items.TryGetValue(u.SkuId, out var item))
                throw new PermanentFailureException($"{label} names SKU {u.SkuId}, which has no stock record.");
            if (item.Serialized)
            {
                var serial = u.Serial is null ? null : Identifiers.NormalizeSerial(u.Serial);
                units.Add((u.SkuId, serial ?? throw new PermanentFailureException($"{label}: serialized SKU {u.SkuId} needs a valid serial."), 1));
            }
            else if (u.Quantity > 0) units.Add((u.SkuId, null, u.Quantity));
            else throw new PermanentFailureException($"{label}: SKU {u.SkuId} has a non-positive quantity.");
        }

        var wanted = units.Where(u => u.Serial is not null).Select(u => u.Serial!).ToList();
        var found = await db.SerialUnits.AsNoTracking().Where(s => wanted.Contains(s.Serial)).ToDictionaryAsync(s => s.Serial, ct);
        foreach (var u in units.Where(u => u.Serial is not null))
            if (!found.TryGetValue(u.Serial!, out var s) || s.SkuId != u.SkuId || s.Status != SerialStatuses.Shipped || (shipmentRefId is not null && s.RefId != shipmentRefId))
                throw new PermanentFailureException($"{label}: serial {u.Serial} is not a shipped unit of SKU {u.SkuId}{(shipmentRefId is null ? "" : " for this shipment")}.");
        if (wanted.Distinct().Count() != wanted.Count)
            throw new PermanentFailureException($"{label} repeats a serial.");
        return units;
    }
}
