using Bloom.Inventory.Consumers;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class OrderPaidTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static OrderPaidV1 Paid(Guid orderId, Guid? launchId, DateTimeOffset paidAt, params (Guid Sku, int Quantity)[] lines) =>
        new(orderId, "BL-1", Guid.CreateVersion7(), launchId, paidAt, 1000, lines.Select(l => new OrderPaidLineV1(l.Sku, l.Quantity)).ToList());

    private static Task PaidAsync(TestInventory t, OrderPaidV1 paid) => Consume.Async<OrderPaidConsumer>(t, OrderMessages.Paid, paid);

    private static async Task ReserveAsync(TestInventory t, Guid orderId, Guid sku, int quantity) =>
        await t.Stock.ReserveStockAsync(new() { OrderId = orderId.ToString(), Lines = { new StockLine { SkuId = sku.ToString(), Quantity = quantity } } }, cancellationToken: Ct);

    private static async Task<List<StockAllocation>> AllocationsAsync(TestInventory t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.StockAllocations.AsNoTracking().Where(a => a.OrderId == orderId).ToListAsync(Ct);
    }

    [Fact]
    public async Task I03_regular_paid_commits_the_reservation()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = Guid.CreateVersion7();
        await ReserveAsync(t, orderId, sku, 2);

        await PaidAsync(t, Paid(orderId, null, DateTimeOffset.UtcNow, (sku, 2)));

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((3, 0, 2), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Committed, Assert.Single(await AllocationsAsync(t, orderId)).Status);
        var entry = (await Seed.LedgerAsync(t, sku)).Last();
        Assert.Equal((EntryTypes.Committed, -2, -2, 2, RefTypes.Order, orderId, "inventory"),
            (entry.EntryType, entry.DOnHand, entry.DReserved, entry.DCommitted, entry.RefType, entry.RefId, entry.Actor));
    }

    [Fact]
    public async Task I17_duplicate_paid_is_applied_once()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var regular = Guid.CreateVersion7();
        await ReserveAsync(t, regular, sku, 2);
        var paid = Paid(regular, null, DateTimeOffset.UtcNow, (sku, 2));
        await PaidAsync(t, paid);
        await PaidAsync(t, paid);

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((3, 0, 2), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(3, (await Seed.LedgerAsync(t, sku)).Count); // received, reserved, committed once

        var launchSku = Seed.Sku(t);
        var preorder = Paid(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow, (launchSku, 1));
        await PaidAsync(t, preorder);
        await PaidAsync(t, preorder);
        Assert.Single(await AllocationsAsync(t, preorder.OrderId));
    }

    [Fact]
    public async Task Paid_for_released_allocation_changes_nothing()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 3);
        var orderId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var db = t.NewDb())
        {
            db.StockAllocations.Add(new StockAllocation
            {
                RefId = orderId, SkuId = sku, OrderId = orderId, Kind = AllocationKinds.Regular, Quantity = 2,
                Status = AllocationStatuses.Released, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
        }

        await PaidAsync(t, Paid(orderId, null, now, (sku, 2)));

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((3, 0, 0), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Released, Assert.Single(await AllocationsAsync(t, orderId)).Status);
        Assert.Single(await Seed.LedgerAsync(t, sku));
    }

    [Fact]
    public async Task Preorder_without_stock_waits_as_backorder()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        var paidAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var orderId = Guid.CreateVersion7();

        await PaidAsync(t, Paid(orderId, Guid.CreateVersion7(), paidAt, (sku, 2), (sku, 1)));

        var a = Assert.Single(await AllocationsAsync(t, orderId));
        Assert.Equal((AllocationKinds.Preorder, AllocationStatuses.Backordered, 3, orderId), (a.Kind, a.Status, a.Quantity, a.RefId)); // lines of one SKU are summed
        Assert.Equal(paidAt, a.PaidAt);
        Assert.Null(await Seed.ItemAsync(t, sku));
        Assert.Empty(await Seed.LedgerAsync(t, sku));
        Assert.Empty(await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated));
    }

    [Fact]
    public async Task Preorder_paid_with_stock_on_hand_commits_now()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = Guid.CreateVersion7();

        await PaidAsync(t, Paid(orderId, Guid.CreateVersion7(), DateTimeOffset.UtcNow, (sku, 2)));

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((3, 0, 2), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Committed, Assert.Single(await AllocationsAsync(t, orderId)).Status);
        Assert.Equal(EntryTypes.BackorderAllocated, (await Seed.LedgerAsync(t, sku)).Last().EntryType);
        Assert.Equal(orderId, Assert.Single(await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated)).OrderId);
    }

    [Fact]
    public async Task Stock_allocated_waits_for_every_preorder_line()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var a = Seed.Sku(t);
        var b = Seed.Sku(t);
        await Seed.ReceiveAsync(t, a, 3);
        var orderId = Guid.CreateVersion7();

        await PaidAsync(t, Paid(orderId, Guid.CreateVersion7(), DateTimeOffset.UtcNow, (a, 1), (b, 1)));

        Assert.Equal([AllocationStatuses.Committed, AllocationStatuses.Backordered],
            (await AllocationsAsync(t, orderId)).OrderBy(x => x.SkuId == a ? 0 : 1).Select(x => x.Status));
        Assert.Empty(await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated));

        await Seed.ReceiveAsync(t, b, 1);

        Assert.Equal(orderId, Assert.Single(await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated)).OrderId);
    }

    [Fact]
    public async Task Preorder_queues_behind_older_backorders()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        var now = DateTimeOffset.UtcNow;
        var older = await Seed.BackorderAsync(t, sku, 3, now.AddHours(-1));
        await Seed.ReceiveAsync(t, sku, 2);
        var orderId = Guid.CreateVersion7();

        await PaidAsync(t, Paid(orderId, Guid.CreateVersion7(), now, (sku, 1)));

        Assert.Equal(AllocationStatuses.Backordered, Assert.Single(await AllocationsAsync(t, orderId)).Status);
        Assert.Equal(AllocationStatuses.Backordered, Assert.Single(await AllocationsAsync(t, older)).Status);
        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((2, 0, 0), (item.OnHand, item.Reserved, item.Committed));
    }

    [Fact]
    public async Task Invalid_payload_is_permanent_failure()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        var launch = Guid.CreateVersion7();

        await Assert.ThrowsAsync<PermanentFailureException>(() => Consume.Async<OrderPaidConsumer>(t, OrderMessages.Paid,
            new { orderNumber = "BL-1", accountId = Guid.NewGuid(), paidAt = DateTimeOffset.UtcNow, totalCents = 1, lines = new[] { new { skuId = sku, quantity = 1 } } }));
        await Assert.ThrowsAsync<PermanentFailureException>(() => PaidAsync(t, Paid(Guid.CreateVersion7(), launch, DateTimeOffset.UtcNow, (sku, 0))));
        await Assert.ThrowsAsync<PermanentFailureException>(() => PaidAsync(t, Paid(Guid.CreateVersion7(), launch, DateTimeOffset.UtcNow)));

        await using var db = t.NewDb();
        Assert.Empty(await db.StockAllocations.ToListAsync(Ct));
        Assert.Empty(await db.StockLedger.ToListAsync(Ct));
    }

    [Fact]
    public async Task First_receipt_and_preorder_paid_concurrently_never_leave_the_backorder_waiting()
    {
        await using var t = await TestInventory.StartAsync(containers);
        for (var round = 0; round < 20; round++)
        {
            // Many SKUs at once: the lost wake-up needs the two transactions to overlap inside a window of a few statements.
            var races = Enumerable.Range(0, 16).Select(async _ =>
            {
                var sku = Seed.Sku(t); // never received
                var orderId = Guid.CreateVersion7();
                await Task.WhenAll(
                    Task.Run(() => Seed.ReceiveAsync(t, sku, 3), Ct),
                    Task.Run(() => PaidAsync(t, Paid(orderId, Guid.CreateVersion7(), DateTimeOffset.UtcNow, (sku, 2))), Ct));
                return orderId;
            }).ToList();
            var orders = await Task.WhenAll(races);

            var announced = (await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated)).Select(m => m.OrderId).ToHashSet();
            foreach (var orderId in orders)
            {
                Assert.Equal(AllocationStatuses.Committed, Assert.Single(await AllocationsAsync(t, orderId)).Status);
                Assert.Contains(orderId, announced);
            }
        }
    }
}
