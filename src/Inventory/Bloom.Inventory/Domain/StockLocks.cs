using Bloom.Inventory.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Domain;

/// <summary>Row locks on <c>stock_items</c>, always one statement in ascending <c>sku_id</c> order (plan R9, 00-reliability §7).</summary>
public sealed class StockLocks(InventoryDbContext db)
{
    /// <summary>Locks the SKUs' rows until the transaction ends. Returns the rows that exist (untracked); SKUs never received have none.</summary>
    public async Task<Dictionary<Guid, StockItem>> LockAsync(IEnumerable<Guid> skuIds, CancellationToken ct)
    {
        var ids = skuIds.Distinct().ToArray();
        return (await db.StockItems.FromSql($"SELECT * FROM stock_items WHERE sku_id = ANY({ids}) ORDER BY sku_id FOR UPDATE").AsNoTracking().ToListAsync(ct))
            .ToDictionary(i => i.SkuId);
    }
}
