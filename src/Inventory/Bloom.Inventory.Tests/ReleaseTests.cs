using Bloom.Inventory.Consumers;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class ReleaseTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Task ReleaseAsync(TestInventory t, Guid orderId) =>
        Consume.Async<ReleaseReservationConsumer>(t, InventoryMessages.ReleaseReservation, new ReleaseReservationV1(orderId));

    private static StockLine Line(Guid sku, int quantity) => new() { SkuId = sku.ToString(), Quantity = quantity };

    private static async Task ReserveAsync(TestInventory t, Guid orderId, params StockLine[] lines) =>
        await t.Stock.ReserveStockAsync(new() { OrderId = orderId.ToString(), Lines = { lines } }, cancellationToken: Ct);

    private static Task PayAsync(TestInventory t, Guid orderId, params (Guid Sku, int Quantity)[] lines) =>
        Consume.Async<OrderPaidConsumer>(t, OrderMessages.Paid,
            new OrderPaidV1(orderId, "BL-1", Guid.CreateVersion7(), null, DateTimeOffset.UtcNow, 1000, lines.Select(l => new OrderPaidLineV1(l.Sku, l.Quantity)).ToList()));

    private static async Task<List<StockAllocation>> AllocationsAsync(TestInventory t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.StockAllocations.AsNoTracking().Where(a => a.OrderId == orderId).ToListAsync(Ct);
    }

    private static async Task<IReadOnlyList<Guid>> ReleasedAsync(TestInventory t) =>
        (await Outbox.PayloadsAsync<ReservationReleasedV1>(t, InventoryMessages.ReservationReleased)).Select(m => m.OrderId).ToList();

    [Fact]
    public async Task Release_of_reserved_frees_available()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = Guid.CreateVersion7();
        await ReserveAsync(t, orderId, Line(sku, 2));

        await ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((5, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await AllocationsAsync(t, orderId)).Status);
        var entry = (await Seed.LedgerAsync(t, sku)).Last();
        Assert.Equal((EntryTypes.ReservationReleased, 0, -2, 0, RefTypes.Order, orderId, "inventory"),
            (entry.EntryType, entry.DOnHand, entry.DReserved, entry.DCommitted, entry.RefType, entry.RefId, entry.Actor));
        Assert.Equal([orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task Release_of_backordered_touches_no_counters()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 1);
        var orderId = await Seed.BackorderAsync(t, sku, 3, DateTimeOffset.UtcNow.AddHours(-1));

        await ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((1, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await AllocationsAsync(t, orderId)).Status);
        Assert.Single(await Seed.LedgerAsync(t, sku)); // only the receipt
        Assert.Equal([orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task I09_release_of_committed_with_assigned_serials()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, sku, 1, ["ABC123"]);
        var orderId = Guid.CreateVersion7();
        await ReserveAsync(t, orderId, Line(sku, 1));
        await PayAsync(t, orderId, (sku, 1));
        await t.Packing.AssignSerialAsync(new() { RefId = orderId.ToString(), SkuId = sku.ToString(), Serial = "ABC123" }, cancellationToken: Ct);
        var waiting = await Seed.BackorderAsync(t, sku, 1, DateTimeOffset.UtcNow.AddHours(-1));

        await ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((0, 0, 1), (item.OnHand, item.Reserved, item.Committed)); // the freed unit went straight to the waiting pre-order
        await using (var db = t.NewDb())
        {
            var serial = await db.SerialUnits.AsNoTracking().SingleAsync(s => s.Serial == "ABC123", Ct);
            Assert.Equal((SerialStatuses.InStock, null), (serial.Status, serial.RefId));
        }
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await AllocationsAsync(t, orderId)).Status);
        Assert.Equal(AllocationStatuses.Committed, Assert.Single(await AllocationsAsync(t, waiting)).Status);
        var ledger = await Seed.LedgerAsync(t, sku);
        Assert.Equal([EntryTypes.CommitmentReleased, EntryTypes.BackorderAllocated], ledger.TakeLast(2).Select(e => e.EntryType));
        var released = ledger[^2];
        Assert.Equal((1, 0, -1, orderId), (released.DOnHand, released.DReserved, released.DCommitted, released.RefId));
        Assert.Equal([waiting], (await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated)).Select(m => m.OrderId));
        Assert.Equal([orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task Release_covers_the_orders_reships()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var (reship, orderId) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await t.Stock.CommitForReshipAsync(new() { ReshipId = reship.ToString(), OrderId = orderId.ToString(), Lines = { Line(sku, 2) } }, cancellationToken: Ct);

        await ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((5, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await AllocationsAsync(t, orderId)).Status);
        var entry = (await Seed.LedgerAsync(t, sku)).Last();
        Assert.Equal((EntryTypes.CommitmentReleased, RefTypes.Reship, reship), (entry.EntryType, entry.RefType, entry.RefId));
        Assert.Equal([orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task Shipped_and_released_allocations_are_untouched()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var other = Seed.Sku(t);
        await using (var db = t.NewDb())
        {
            db.StockAllocations.Add(new StockAllocation { RefId = orderId, SkuId = sku, OrderId = orderId, Kind = AllocationKinds.Regular, Quantity = 1, Status = AllocationStatuses.Shipped, CreatedAt = now, UpdatedAt = now });
            db.StockAllocations.Add(new StockAllocation { RefId = orderId, SkuId = other, OrderId = orderId, Kind = AllocationKinds.Regular, Quantity = 1, Status = AllocationStatuses.Released, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(Ct);
        }

        await ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((5, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal([AllocationStatuses.Released, AllocationStatuses.Shipped], (await AllocationsAsync(t, orderId)).Select(a => a.Status).Order());
        Assert.Single(await Seed.LedgerAsync(t, sku));
        Assert.Equal([orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task Release_of_unknown_order_still_publishes_released()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var orderId = Guid.CreateVersion7();

        await ReleaseAsync(t, orderId);

        Assert.Equal([orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task Repeated_release_changes_nothing_more()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = Guid.CreateVersion7();
        await ReserveAsync(t, orderId, Line(sku, 2));

        await ReleaseAsync(t, orderId);
        await ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((5, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Single(await Seed.LedgerAsync(t, sku), e => e.EntryType == EntryTypes.ReservationReleased);
        Assert.Equal([orderId, orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task Release_and_receipt_on_shared_skus_never_deadlock()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (a, b) = (Seed.Sku(t), Seed.Sku(t));
        await Seed.ReceiveAsync(t, a, 100);
        await Seed.ReceiveAsync(t, b, 100);
        for (var round = 0; round < 30; round++)
        {
            var orderId = Guid.CreateVersion7();
            await ReserveAsync(t, orderId, Line(a, 1), Line(b, 1));
            var receipt = new { lines = new[] { new { skuId = b, quantity = 1, serials = Array.Empty<string>() }, new { skuId = a, quantity = 1, serials = Array.Empty<string>() } } };

            await Task.WhenAll(
                Task.Run(() => ReleaseAsync(t, orderId), Ct),
                Task.Run(async () =>
                {
                    using var response = await t.SendAsync(HttpMethod.Post, "/api/admin/inventory/receipts", receipt, TestCaller.Staff("warehouse"), Guid.NewGuid());
                    await response.OkJsonAsync();
                }, Ct));
        }

        foreach (var sku in new[] { a, b })
        {
            var item = (await Seed.ItemAsync(t, sku))!;
            Assert.Equal((130, 0), (item.OnHand, item.Reserved));
        }
    }
}
