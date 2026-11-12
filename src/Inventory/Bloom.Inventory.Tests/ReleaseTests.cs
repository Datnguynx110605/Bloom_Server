using Bloom.Inventory.Consumers;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class ReleaseTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IReadOnlyList<Guid>> ReleasedAsync(TestInventory t) =>
        (await Outbox.PayloadsAsync<ReservationReleasedV1>(t, InventoryMessages.ReservationReleased)).Select(m => m.OrderId).ToList();

    [Fact]
    public async Task Release_of_reserved_frees_available()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = Guid.CreateVersion7();
        await Seed.ReserveAsync(t, orderId, (sku, 2));

        await Seed.ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((5, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await Seed.AllocationsAsync(t, orderId)).Status);
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

        await Seed.ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((1, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await Seed.AllocationsAsync(t, orderId)).Status);
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
        await Seed.ReserveAsync(t, orderId, (sku, 1));
        await Seed.PaidAsync(t, orderId, null, (sku, 1));
        await t.Packing.AssignSerialAsync(new() { RefId = orderId.ToString(), SkuId = sku.ToString(), Serial = "ABC123" }, cancellationToken: Ct);
        var waiting = await Seed.BackorderAsync(t, sku, 1, DateTimeOffset.UtcNow.AddHours(-1));

        await Seed.ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((0, 0, 1), (item.OnHand, item.Reserved, item.Committed)); // the freed unit went straight to the waiting pre-order
        await using (var db = t.NewDb())
        {
            var serial = await db.SerialUnits.AsNoTracking().SingleAsync(s => s.Serial == "ABC123", Ct);
            Assert.Equal((SerialStatuses.InStock, null), (serial.Status, serial.RefId));
        }
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await Seed.AllocationsAsync(t, orderId)).Status);
        Assert.Equal(AllocationStatuses.Committed, Assert.Single(await Seed.AllocationsAsync(t, waiting)).Status);
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
        await t.Stock.CommitForReshipAsync(new() { ReshipId = reship.ToString(), OrderId = orderId.ToString(), Lines = { Seed.Line(sku, 2) } }, cancellationToken: Ct);

        await Seed.ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((5, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await Seed.AllocationsAsync(t, orderId)).Status);
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

        await Seed.ReleaseAsync(t, orderId);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((5, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal([AllocationStatuses.Released, AllocationStatuses.Shipped], (await Seed.AllocationsAsync(t, orderId)).Select(a => a.Status).Order());
        Assert.Single(await Seed.LedgerAsync(t, sku));
        Assert.Equal([orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task Release_of_unknown_order_still_publishes_released()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var orderId = Guid.CreateVersion7();

        await Seed.ReleaseAsync(t, orderId);

        Assert.Equal([orderId], await ReleasedAsync(t));
    }

    [Fact]
    public async Task Repeated_release_changes_nothing_more()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = Guid.CreateVersion7();
        await Seed.ReserveAsync(t, orderId, (sku, 2));

        await Seed.ReleaseAsync(t, orderId);
        await Seed.ReleaseAsync(t, orderId);

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
            await Seed.ReserveAsync(t, orderId, (a, 1), (b, 1));
            var receipt = new { lines = new[] { new { skuId = b, quantity = 1, serials = Array.Empty<string>() }, new { skuId = a, quantity = 1, serials = Array.Empty<string>() } } };

            await Task.WhenAll(
                Task.Run(() => Seed.ReleaseAsync(t, orderId), Ct),
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

    [Fact]
    public async Task D16_release_without_allocations_still_tombstones()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = Guid.CreateVersion7();

        await Seed.ReleaseAsync(t, orderId); // never reserved, never paid

        Assert.Equal(1, await Seed.TombstonesAsync(t, orderId));
        Assert.Equal([orderId], await ReleasedAsync(t));
        await Seed.PaidAsync(t, orderId, Guid.CreateVersion7(), (sku, 1)); // a later pre-order paid creates nothing
        Assert.Empty(await Seed.AllocationsAsync(t, orderId));
        await Rpc.FailsAsync(t.Stock.ReserveStockAsync(new() { OrderId = orderId.ToString(), Lines = { Seed.Line(sku, 1) } }, cancellationToken: Ct), "ORDER_RELEASED");
        Assert.Equal(0, (await Seed.ItemAsync(t, sku))!.Reserved);
    }

    [Fact]
    public async Task D16_duplicate_release_is_idempotent()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var orderId = Guid.CreateVersion7();

        await Seed.ReleaseAsync(t, orderId);
        await Seed.ReleaseAsync(t, orderId);

        Assert.Equal(1, await Seed.TombstonesAsync(t, orderId));
    }
}
