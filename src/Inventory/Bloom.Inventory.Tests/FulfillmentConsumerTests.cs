using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Consumers;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class FulfillmentConsumerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Reserves, pays and (for serialized SKUs) assigns the given serials: a committed allocation per line. Returns the order id.</summary>
    private static async Task<Guid> CommittedAsync(TestInventory t, (Guid Sku, int Quantity)[] lines, params (Guid Sku, string Serial)[] assigned)
    {
        var orderId = Guid.CreateVersion7();
        await Seed.ReserveAsync(t, orderId, lines);
        await Seed.PaidAsync(t, orderId, null, lines);
        foreach (var (sku, serial) in assigned)
            await t.Packing.AssignSerialAsync(new() { RefId = orderId.ToString(), SkuId = sku.ToString(), Serial = serial }, cancellationToken: Ct);
        return orderId;
    }

    /// <summary>A serialized SKU with serials ABC123 … ABC125, 2 of them sold to a paid order and assigned.</summary>
    private static async Task<(Guid Sku, Guid OrderId)> SoldSerializedAsync(TestInventory t, bool assignAll = true)
    {
        var sku = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, sku, 3, ["ABC123", "ABC124", "ABC125"]);
        var orderId = await CommittedAsync(t, [(sku, 2)], assignAll ? [(sku, "ABC123"), (sku, "ABC124")] : [(sku, "ABC123")]);
        return (sku, orderId);
    }

    private static Task ReturnAsync(TestInventory t, Guid orderId, params ReturnedUnitV1[] units) =>
        Consume.Async<CustomerReturnConsumer>(t, FulfillmentMessages.ReceiveCustomerReturn, new ReceiveCustomerReturnV1(orderId, units));

    private static Task RtsAsync(TestInventory t, Guid refId, params ReturnedToSenderUnitV1[] units) =>
        Consume.Async<ReturnedToSenderConsumer>(t, FulfillmentMessages.ReceiveReturnedToSender, new ReceiveReturnedToSenderV1(refId, units));

    private static Task LostAsync(TestInventory t, Guid refId) =>
        Consume.Async<LostInTransitConsumer>(t, FulfillmentMessages.MarkLostInTransit, new MarkLostInTransitV1(refId));

    private static async Task<int> AvailableAsync(TestInventory t, Guid sku) =>
        (await t.Stock.GetAvailabilityAsync(new() { SkuIds = { sku.ToString() } }, cancellationToken: Ct)).Items.Single().Available;

    [Fact]
    public async Task Shipment_moves_committed_to_shipped()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t);

        await Seed.ShipAsync(t, orderId, orderId, (sku, 2));

        Assert.Equal((1, 0, 0, 0, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Equal(AllocationStatuses.Shipped, Assert.Single(await Seed.AllocationsAsync(t, orderId)).Status);
        Assert.Equal([SerialStatuses.Shipped, SerialStatuses.Shipped, SerialStatuses.InStock],
            [(await Seed.SerialAsync(t, "ABC123")).Status, (await Seed.SerialAsync(t, "ABC124")).Status, (await Seed.SerialAsync(t, "ABC125")).Status]);
        var entry = (await Seed.LedgerAsync(t, sku)).Last();
        Assert.Equal((EntryTypes.Shipped, 0, 0, -2, RefTypes.Order, orderId, "inventory"),
            (entry.EntryType, entry.DOnHand, entry.DReserved, entry.DCommitted, entry.RefType, entry.RefId, entry.Actor));
    }

    [Fact]
    public async Task I12_shipment_with_missing_serials_is_permanent_and_changes_nothing()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t, assignAll: false);
        var ledgerBefore = (await Seed.LedgerAsync(t, sku)).Count;

        await Assert.ThrowsAsync<PermanentFailureException>(() => Seed.ShipAsync(t, orderId, orderId, (sku, 2)));

        Assert.Equal((1, 0, 2, 0, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Equal(AllocationStatuses.Committed, Assert.Single(await Seed.AllocationsAsync(t, orderId)).Status);
        Assert.Equal(SerialStatuses.Assigned, (await Seed.SerialAsync(t, "ABC123")).Status);
        Assert.Equal(ledgerBefore, (await Seed.LedgerAsync(t, sku)).Count);
    }

    [Fact]
    public async Task Shipment_for_unknown_ref_changes_nothing()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t);
        var ledgerBefore = (await Seed.LedgerAsync(t, sku)).Count;

        await Seed.ShipAsync(t, orderId, Guid.CreateVersion7(), (sku, 2));

        Assert.Equal((1, 0, 2, 0, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Equal(AllocationStatuses.Committed, Assert.Single(await Seed.AllocationsAsync(t, orderId)).Status);
        Assert.Equal(ledgerBefore, (await Seed.LedgerAsync(t, sku)).Count);
    }

    [Fact]
    public async Task Non_serialized_shipment_needs_no_serials()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = await CommittedAsync(t, [(sku, 2)]);

        await Seed.ShipAsync(t, orderId, orderId, (sku, 2));

        Assert.Equal((3, 0, 0, 0, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Equal(AllocationStatuses.Shipped, Assert.Single(await Seed.AllocationsAsync(t, orderId)).Status);
        Assert.Equal(EntryTypes.Shipped, (await Seed.LedgerAsync(t, sku)).Last().EntryType);
    }

    [Fact]
    public async Task I13_customer_return_is_held_not_sellable()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t);
        await Seed.ShipAsync(t, orderId, orderId, (sku, 2));
        var availableBefore = await AvailableAsync(t, sku);

        await ReturnAsync(t, orderId, new ReturnedUnitV1(sku, "abc123", 1));

        Assert.Equal(SerialStatuses.Returned, (await Seed.SerialAsync(t, "ABC123")).Status);
        Assert.Equal((1, 0, 0, 1, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Equal(availableBefore, await AvailableAsync(t, sku));
        var entry = (await Seed.LedgerAsync(t, sku)).Last();
        Assert.Equal((EntryTypes.CustomerReturned, 0, 1, RefTypes.Order, orderId), (entry.EntryType, entry.DOnHand, entry.DReturned, entry.RefType, entry.RefId));
    }

    [Fact]
    public async Task Customer_return_of_unshipped_serial_is_permanent()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t);
        await Seed.ShipAsync(t, orderId, orderId, (sku, 2));

        // ABC124 is shipped and valid, ABC125 never left stock: the whole message fails before the first write.
        await Assert.ThrowsAsync<PermanentFailureException>(() =>
            ReturnAsync(t, orderId, new ReturnedUnitV1(sku, "ABC124", 1), new ReturnedUnitV1(sku, "ABC125", 1)));

        Assert.Equal(0, (await Seed.ItemAsync(t, sku))!.Returned);
        Assert.Equal(SerialStatuses.Shipped, (await Seed.SerialAsync(t, "ABC124")).Status);
        Assert.Equal(SerialStatuses.InStock, (await Seed.SerialAsync(t, "ABC125")).Status);
    }

    [Fact]
    public async Task I14_returned_to_sender_sealed_restocks_and_runs_allocator()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t);
        await Seed.ShipAsync(t, orderId, orderId, (sku, 2));
        var waiting = await Seed.BackorderAsync(t, sku, 2, DateTimeOffset.UtcNow.AddHours(-1)); // on hand 1: waits for one more unit

        await RtsAsync(t, orderId, new ReturnedToSenderUnitV1(sku, "ABC123", 1, "sealed"));

        var serial = await Seed.SerialAsync(t, "ABC123");
        Assert.Equal((SerialStatuses.InStock, null), (serial.Status, serial.RefId));
        Assert.Equal((0, 0, 2, 0, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!)); // restocked, then both units went to the waiting pre-order
        Assert.Equal(AllocationStatuses.Committed, Assert.Single(await Seed.AllocationsAsync(t, waiting)).Status);
        var ledger = await Seed.LedgerAsync(t, sku);
        Assert.Equal([EntryTypes.RtsRestocked, EntryTypes.BackorderAllocated], ledger.TakeLast(2).Select(e => e.EntryType));
        Assert.Equal((1, RefTypes.Order, orderId), (ledger[^2].DOnHand, ledger[^2].RefType, ledger[^2].RefId));
        Assert.Equal([waiting], (await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated)).Select(m => m.OrderId));
    }

    [Fact]
    public async Task Returned_to_sender_failed_is_damaged()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t);
        await Seed.ShipAsync(t, orderId, orderId, (sku, 2));

        await RtsAsync(t, orderId, new ReturnedToSenderUnitV1(sku, "ABC123", 1, "failed"));

        Assert.Equal(SerialStatuses.Damaged, (await Seed.SerialAsync(t, "ABC123")).Status);
        Assert.Equal((1, 0, 0, 0, 1, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
        var entry = (await Seed.LedgerAsync(t, sku)).Last();
        Assert.Equal((EntryTypes.RtsDamaged, 1, RefTypes.Order, orderId), (entry.EntryType, entry.DDamaged, entry.RefType, entry.RefId));
    }

    [Fact]
    public async Task Returned_to_sender_of_another_orders_serial_is_permanent()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t);
        await Seed.ShipAsync(t, orderId, orderId, (sku, 2));

        await Assert.ThrowsAsync<PermanentFailureException>(() => RtsAsync(t, Guid.CreateVersion7(), new ReturnedToSenderUnitV1(sku, "ABC123", 1, "sealed")));
        await Assert.ThrowsAsync<PermanentFailureException>(() => RtsAsync(t, orderId, new ReturnedToSenderUnitV1(sku, "ABC123", 1, "broken")));

        Assert.Equal((1, 0, 0, 0, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Equal(SerialStatuses.Shipped, (await Seed.SerialAsync(t, "ABC123")).Status);
    }

    [Fact]
    public async Task Non_serialized_returns_use_the_quantity()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = await CommittedAsync(t, [(sku, 3)]);
        await Seed.ShipAsync(t, orderId, orderId, (sku, 3));

        await ReturnAsync(t, orderId, new ReturnedUnitV1(sku, null, 1));
        await RtsAsync(t, orderId, new ReturnedToSenderUnitV1(sku, null, 2, "sealed"));

        Assert.Equal((4, 0, 0, 1, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
    }

    [Fact]
    public async Task Lost_in_transit_marks_serials_and_counts()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (serialized, plain) = (Seed.Sku(t, serialized: true), Seed.Sku(t));
        await Seed.ReceiveAsync(t, serialized, 2, ["ABC123", "ABC124"]);
        await Seed.ReceiveAsync(t, plain, 5);
        var orderId = await CommittedAsync(t, [(serialized, 2), (plain, 3)], (serialized, "ABC123"), (serialized, "ABC124"));
        await Seed.ShipAsync(t, orderId, orderId, (serialized, 2), (plain, 3));

        await LostAsync(t, orderId);

        Assert.Equal([SerialStatuses.Lost, SerialStatuses.Lost], [(await Seed.SerialAsync(t, "ABC123")).Status, (await Seed.SerialAsync(t, "ABC124")).Status]);
        Assert.Equal((0, 0, 0, 0, 0, 2), Seed.Counters((await Seed.ItemAsync(t, serialized))!));
        Assert.Equal((2, 0, 0, 0, 0, 3), Seed.Counters((await Seed.ItemAsync(t, plain))!));
        var entry = (await Seed.LedgerAsync(t, plain)).Last();
        Assert.Equal((EntryTypes.LostInTransit, 3, RefTypes.Order, orderId), (entry.EntryType, entry.DLost, entry.RefType, entry.RefId));
        Assert.All(await Seed.AllocationsAsync(t, orderId), a => Assert.Equal(AllocationStatuses.Shipped, a.Status));
    }

    [Fact]
    public async Task Lost_in_transit_for_unknown_ref_is_permanent()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await SoldSerializedAsync(t); // committed, not shipped

        await Assert.ThrowsAsync<PermanentFailureException>(() => LostAsync(t, Guid.CreateVersion7()));
        await Assert.ThrowsAsync<PermanentFailureException>(() => LostAsync(t, orderId));

        Assert.Equal((1, 0, 2, 0, 0, 0), Seed.Counters((await Seed.ItemAsync(t, sku))!));
    }

    [Fact]
    public async Task IN7_second_lost_in_transit_command_does_not_double_count_lost()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var orderId = await CommittedAsync(t, [(sku, 3)]);
        await Seed.ShipAsync(t, orderId, orderId, (sku, 3));

        await LostAsync(t, orderId);
        await LostAsync(t, orderId); // another message, the same units

        Assert.Equal((2, 0, 0, 0, 0, 3), Seed.Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Single(await Seed.LedgerAsync(t, sku), e => e.EntryType == EntryTypes.LostInTransit);
    }
}
