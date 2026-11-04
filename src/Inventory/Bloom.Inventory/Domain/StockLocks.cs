using Bloom.Inventory.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Domain;

/// <summary>Row locks on <c>stock_items</c>, always one statement in ascending <c>sku_id</c> order (plan R9, 00-reliability §7).</summary>
public sealed class StockLocks(InventoryDbContext db)
{
    /// <summary>
    /// Locks the SKUs until the transaction ends: first one transaction-scoped advisory lock per SKU (ascending key order), so callers serialize
    /// even before the SKU's first receipt has a committed <c>stock_items</c> row to lock; then the rows themselves. Returns the rows that exist (untracked).
    /// </summary>
    public async Task<Dictionary<Guid, StockItem>> LockAsync(IEnumerable<Guid> skuIds, CancellationToken ct)
    {
        var ids = skuIds.Distinct().ToArray();
        // Both halves of the uuid, so UUIDv7 ids created in the same millisecond still get distinct keys. A collision only over-serializes; ascending key order keeps it deadlock-free.
        var keys = ids.Select(id => { var b = id.ToByteArray(bigEndian: true); return BitConverter.ToInt64(b, 0) ^ BitConverter.ToInt64(b, 8); }).Distinct().ToArray();
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(s.k) FROM (SELECT k FROM unnest({keys}) AS k ORDER BY k OFFSET 0) AS s", ct);
        return (await db.StockItems.FromSql($"SELECT * FROM stock_items WHERE sku_id = ANY({ids}) ORDER BY sku_id FOR UPDATE").AsNoTracking().ToListAsync(ct))
            .ToDictionary(i => i.SkuId);
    }
}
