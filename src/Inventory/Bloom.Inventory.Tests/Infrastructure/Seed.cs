using Bloom.Inventory.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

/// <summary>SKUs, stock and backorders for tests: stock goes in through the API, backorders straight into the database.</summary>
public static class Seed
{
    /// <summary>A SKU known to the fake catalog.</summary>
    public static Guid Sku(TestInventory t, bool serialized = false)
    {
        var id = Guid.CreateVersion7();
        t.Catalog.AddSku(id, Guid.CreateVersion7(), serialized);
        return id;
    }

    /// <summary>A goods receipt for one SKU through the API as staff <c>warehouse</c>; returns the receipt id.</summary>
    public static async Task<Guid> ReceiveAsync(TestInventory t, Guid skuId, int quantity, string[]? serials = null)
    {
        var body = new { lines = new[] { new { skuId, quantity, serials = serials ?? [] } } };
        using var response = await t.SendAsync(HttpMethod.Post, "/api/admin/inventory/receipts", body, TestCaller.Staff("warehouse"), Guid.NewGuid());
        return Guid.Parse((await response.OkJsonAsync()).GetProperty("receiptId").GetString()!);
    }

    /// <summary>A paid pre-order waiting for stock: a <c>preorder</c> / <c>backordered</c> allocation. Returns the order id.</summary>
    public static async Task<Guid> BackorderAsync(TestInventory t, Guid skuId, int quantity, DateTimeOffset paidAt)
    {
        var orderId = Guid.CreateVersion7();
        await using var db = t.NewDb();
        db.StockAllocations.Add(new StockAllocation
        {
            RefId = orderId, SkuId = skuId, OrderId = orderId, Kind = AllocationKinds.Preorder, Quantity = quantity,
            Status = AllocationStatuses.Backordered, PaidAt = paidAt, CreatedAt = paidAt, UpdatedAt = paidAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return orderId;
    }

    public static async Task<StockItem?> ItemAsync(TestInventory t, Guid skuId)
    {
        await using var db = t.NewDb();
        return await db.StockItems.AsNoTracking().SingleOrDefaultAsync(i => i.SkuId == skuId, TestContext.Current.CancellationToken);
    }

    /// <summary>The SKU's ledger rows by <c>(created_at, id)</c>.</summary>
    public static async Task<List<LedgerEntry>> LedgerAsync(TestInventory t, Guid skuId)
    {
        await using var db = t.NewDb();
        return await db.StockLedger.AsNoTracking().Where(e => e.SkuId == skuId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
