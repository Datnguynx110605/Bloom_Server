using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Consumers;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

/// <summary>SKUs, stock, backorders and the order-side events for tests: stock goes in through the API, backorders straight into the database, events through the consumers.</summary>
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

    /// <summary>A paid pre-order waiting for stock: a <c>preorder</c> / <c>backordered</c> allocation. Returns the order id; <paramref name="existingOrderId"/> adds a line to that order.</summary>
    public static async Task<Guid> BackorderAsync(TestInventory t, Guid skuId, int quantity, DateTimeOffset paidAt, Guid? existingOrderId = null)
    {
        var orderId = existingOrderId ?? Guid.CreateVersion7();
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

    public static StockLine Line(Guid sku, int quantity) => new() { SkuId = sku.ToString(), Quantity = quantity };

    /// <summary>Reserves the lines for a regular order through the Stock API.</summary>
    public static Task ReserveAsync(TestInventory t, Guid orderId, params (Guid Sku, int Quantity)[] lines) =>
        t.Stock.ReserveStockAsync(new() { OrderId = orderId.ToString(), Lines = { lines.Select(l => Line(l.Sku, l.Quantity)) } }, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;

    public static OrderPaidV1 Paid(Guid orderId, Guid? launchId, DateTimeOffset paidAt, params (Guid Sku, int Quantity)[] lines) =>
        new(orderId, "BL-1", Guid.CreateVersion7(), launchId, paidAt, 1000, lines.Select(l => new OrderPaidLineV1(l.Sku, l.Quantity)).ToList());

    public static Task PaidAsync(TestInventory t, OrderPaidV1 paid) => Consume.Async<OrderPaidConsumer>(t, OrderMessages.Paid, paid);

    /// <summary><c>order.paid</c> now; a non-null <paramref name="launchId"/> makes it a pre-order.</summary>
    public static Task PaidAsync(TestInventory t, Guid orderId, Guid? launchId, params (Guid Sku, int Quantity)[] lines) =>
        PaidAsync(t, Paid(orderId, launchId, DateTimeOffset.UtcNow, lines));

    public static Task ReleaseAsync(TestInventory t, Guid orderId) =>
        Consume.Async<ReleaseReservationConsumer>(t, InventoryMessages.ReleaseReservation, new ReleaseReservationV1(orderId));

    public static Task ShipAsync(TestInventory t, Guid orderId, Guid refId, params (Guid Sku, int Quantity)[] lines) =>
        Consume.Async<ShipmentShippedConsumer>(t, FulfillmentMessages.ShipmentShipped,
            new ShipmentShippedV1(orderId, refId, lines.Select(l => new ShippedLineV1(l.Sku, l.Quantity, [])).ToList(), "TRK1"));

    public static async Task<SerialUnit> SerialAsync(TestInventory t, string serial)
    {
        await using var db = t.NewDb();
        return await db.SerialUnits.AsNoTracking().SingleAsync(s => s.Serial == serial, TestContext.Current.CancellationToken);
    }

    public static async Task<List<StockAllocation>> AllocationsAsync(TestInventory t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.StockAllocations.AsNoTracking().Where(a => a.OrderId == orderId).ToListAsync(TestContext.Current.CancellationToken);
    }

    public static (int OnHand, int Reserved, int Committed, int Returned, int Damaged, int Lost) Counters(StockItem i) =>
        (i.OnHand, i.Reserved, i.Committed, i.Returned, i.Damaged, i.Lost);

    /// <summary>A 15-digit IMEI with a valid Luhn check digit, built from <paramref name="n"/>.</summary>
    public static string Imei(int n)
    {
        var body = $"35{n:D12}";
        var sum = body.Reverse().Select((c, i) => (c - '0') * (i % 2 == 0 ? 2 : 1)).Sum(d => d / 10 + d % 10);
        return body + (10 - sum % 10) % 10;
    }
}
