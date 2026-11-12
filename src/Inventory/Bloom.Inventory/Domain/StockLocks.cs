using Bloom.Inventory.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Domain;

/// <summary>Row locks on <c>stock_items</c>, always one statement in ascending <c>sku_id</c> order (plan R9, 00-reliability §7).</summary>
public sealed class StockLocks(InventoryDbContext db)
{
    /// <summary>The advisory-lock key of a SKU: both halves of the uuid, so UUIDv7 ids created in the same millisecond still get distinct keys.</summary>
    public static long Key(Guid skuId)
    {
        var b = skuId.ToByteArray(bigEndian: true);
        return BitConverter.ToInt64(b, 0) ^ BitConverter.ToInt64(b, 8);
    }

    /// <summary>
    /// The per-order lock (IR-05, 02-inventory §7): taken first by the release consumer, <c>ReserveStock</c> and <c>order.paid</c>, before the tombstone check and any SKU lock.
    /// </summary>
    // ponytail: the order id folds to the same single-bigint key space as the SKU keys; a 2^-64 collision only over-serializes and Postgres' deadlock detector covers a crossed pair. Add a namespace int if it ever matters.
    public Task LockOrderAsync(Guid orderId, CancellationToken ct) => db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Key(orderId)})", ct);

    /// <summary>
    /// Locks the SKUs until the transaction ends: first one transaction-scoped advisory lock per SKU (ascending key order), so callers serialize
    /// even before the SKU's first receipt has a committed <c>stock_items</c> row to lock; then the rows themselves. Returns the rows that exist (untracked).
    /// </summary>
    public async Task<Dictionary<Guid, StockItem>> LockAsync(IEnumerable<Guid> skuIds, CancellationToken ct)
    {
        var ids = skuIds.Distinct().ToArray();
        // A collision only over-serializes; ascending key order keeps it deadlock-free.
        var keys = ids.Select(Key).Distinct().Order();
        foreach (var key in keys) await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct); // one statement per key: the order is ours, not the planner's
        return (await db.StockItems.FromSql($"SELECT * FROM stock_items WHERE sku_id = ANY({ids}) ORDER BY sku_id FOR UPDATE").AsNoTracking().ToListAsync(ct))
            .ToDictionary(i => i.SkuId);
    }
}
