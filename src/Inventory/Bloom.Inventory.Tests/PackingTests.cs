using Bloom.Inventory.Consumers;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Order.Contracts;
using Google.Rpc;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class PackingTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Serials = ["ABC123", "ABC124", "ABC125"];

    private static ErrorInfo Info(RpcException e) => e.GetRpcStatus()!.Details.Single(d => d.Is(ErrorInfo.Descriptor)).Unpack<ErrorInfo>();

    private static async Task<ErrorInfo> FailsAsync<T>(AsyncUnaryCall<T> call, string reason)
    {
        var e = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
        var info = Info(e);
        Assert.Equal(reason, info.Reason);
        return info;
    }

    private static Metadata Staff(TestCaller caller) => new()
    {
        { "x-bloom-user-id", caller.Id.ToString("D") }, { "x-bloom-user-type", caller.Type }, { "x-bloom-role", caller.Role! },
    };

    private static AssignSerialRequest Assign(Guid orderId, Guid sku, string serial) =>
        new() { RefId = orderId.ToString(), SkuId = sku.ToString(), Serial = serial };

    /// <summary>A serialized SKU received with three serials, 2 reserved by a regular order, and that order paid: a committed allocation of 2.</summary>
    private static async Task<(Guid Sku, Guid OrderId)> CommittedAsync(TestInventory t, bool pay = true)
    {
        var sku = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, sku, 3, Serials);
        var orderId = Guid.CreateVersion7();
        await t.Stock.ReserveStockAsync(new() { OrderId = orderId.ToString(), Lines = { new StockLine { SkuId = sku.ToString(), Quantity = 2 } } }, cancellationToken: Ct);
        if (pay)
            await Consume.Async<OrderPaidConsumer>(t, OrderMessages.Paid,
                new OrderPaidV1(orderId, "BL-1", Guid.CreateVersion7(), null, DateTimeOffset.UtcNow, 1000, [new OrderPaidLineV1(sku, 2)]));
        return (sku, orderId);
    }

    private static async Task<SerialUnit> SerialAsync(TestInventory t, string serial)
    {
        await using var db = t.NewDb();
        return await db.SerialUnits.AsNoTracking().SingleAsync(s => s.Serial == serial, Ct);
    }

    private static async Task<List<Bloom.Shared.DeadLetters.StaffActionRecorded>> SerialAuditAsync(TestInventory t) =>
        (await Outbox.AuditAsync(t)).Where(a => a.Action.StartsWith("inventory.serial-")).ToList();

    [Fact]
    public async Task Assign_marks_the_serial_assigned_and_audits()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);
        var staff = TestCaller.Staff("warehouse");
        var ledgerBefore = (await Seed.LedgerAsync(t, sku)).Count;

        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC123"), Staff(staff), cancellationToken: Ct);

        var unit = await SerialAsync(t, "ABC123");
        Assert.Equal((SerialStatuses.Assigned, orderId), (unit.Status, unit.RefId));
        var audit = Assert.Single(await SerialAuditAsync(t));
        Assert.Equal((staff.Id, "warehouse", "inventory", "inventory.serial-assigned", "serial", "ABC123"),
            (audit.ActorId, audit.ActorRole, audit.Service, audit.Action, audit.TargetType, audit.TargetId));
        Assert.Equal(orderId.ToString(), audit.Details.GetProperty("refId").GetString());
        Assert.Equal(ledgerBefore, (await Seed.LedgerAsync(t, sku)).Count);
    }

    [Fact]
    public async Task Assign_retry_is_ok()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);

        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC123"), Staff(TestCaller.Staff("warehouse")), cancellationToken: Ct);
        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC123"), Staff(TestCaller.Staff("warehouse")), cancellationToken: Ct);

        Assert.Equal(1, (await SerialAsync(t, "ABC123")).Version);
        Assert.Single(await SerialAuditAsync(t));
    }

    [Fact]
    public async Task I10_assign_serial_of_another_sku_is_SERIAL_NOT_AVAILABLE()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);
        var other = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, other, 1, ["ZZZ999"]);

        await FailsAsync(t.Packing.AssignSerialAsync(Assign(orderId, sku, "ZZZ999"), cancellationToken: Ct), "SERIAL_NOT_AVAILABLE");
        await FailsAsync(t.Packing.AssignSerialAsync(Assign(orderId, sku, "NOSUCH1"), cancellationToken: Ct), "SERIAL_NOT_AVAILABLE");

        Assert.Equal(SerialStatuses.InStock, (await SerialAsync(t, "ZZZ999")).Status);
    }

    [Fact]
    public async Task I11_assign_more_serials_than_quantity_is_SERIAL_COUNT_EXCEEDED()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);

        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC123"), cancellationToken: Ct);
        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC124"), cancellationToken: Ct);
        await FailsAsync(t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC125"), cancellationToken: Ct), "SERIAL_COUNT_EXCEEDED");

        Assert.Equal(SerialStatuses.InStock, (await SerialAsync(t, "ABC125")).Status);
    }

    [Fact]
    public async Task Concurrent_assigns_never_exceed_quantity()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);

        var results = await Task.WhenAll(Serials.Select(async s =>
        {
            try
            {
                await t.Packing.AssignSerialAsync(Assign(orderId, sku, s), cancellationToken: Ct);
                return "ok";
            }
            catch (RpcException e) { return Info(e).Reason; }
        }));

        Assert.Equal(2, results.Count(r => r == "ok"));
        Assert.Equal(1, results.Count(r => r == "SERIAL_COUNT_EXCEEDED"));
        await using var db = t.NewDb();
        Assert.Equal(2, await db.SerialUnits.CountAsync(s => s.RefId == orderId && s.Status == SerialStatuses.Assigned, Ct));
    }

    [Fact]
    public async Task Assign_without_committed_allocation_is_NOT_COMMITTED()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, reserved) = await CommittedAsync(t, pay: false);

        await FailsAsync(t.Packing.AssignSerialAsync(Assign(reserved, sku, "ABC123"), cancellationToken: Ct), "NOT_COMMITTED");
        await FailsAsync(t.Packing.AssignSerialAsync(Assign(Guid.CreateVersion7(), sku, "ABC123"), cancellationToken: Ct), "NOT_COMMITTED");

        Assert.Equal(SerialStatuses.InStock, (await SerialAsync(t, "ABC123")).Status);
    }

    [Fact]
    public async Task Unassign_and_replace()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);
        var staff = Staff(TestCaller.Staff("warehouse"));
        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC123"), staff, cancellationToken: Ct);

        var unassign = new UnassignSerialRequest { RefId = orderId.ToString(), Serial = "ABC123" };
        await t.Packing.UnassignSerialAsync(unassign, staff, cancellationToken: Ct);
        var unit = await SerialAsync(t, "ABC123");
        Assert.Equal((SerialStatuses.InStock, (Guid?)null), (unit.Status, unit.RefId));
        await t.Packing.UnassignSerialAsync(unassign, staff, cancellationToken: Ct); // retry
        Assert.Equal(2, (await SerialAuditAsync(t)).Count); // assigned, unassigned: the retry wrote nothing

        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC123"), staff, cancellationToken: Ct);
        var replace = new ReplaceSerialRequest { RefId = orderId.ToString(), OldSerial = "ABC123", NewSerial = "ABC125" };
        await t.Packing.ReplaceSerialAsync(replace, staff, cancellationToken: Ct);
        Assert.Equal((SerialStatuses.InStock, SerialStatuses.Assigned, (Guid?)orderId), ((await SerialAsync(t, "ABC123")).Status, (await SerialAsync(t, "ABC125")).Status, (await SerialAsync(t, "ABC125")).RefId));
        await t.Packing.ReplaceSerialAsync(replace, staff, cancellationToken: Ct); // retry

        var audits = await SerialAuditAsync(t);
        Assert.Equal(4, audits.Count);
        Assert.Equal(["inventory.serial-assigned", "inventory.serial-unassigned", "inventory.serial-assigned", "inventory.serial-replaced"], audits.Select(a => a.Action));
        var replaced = audits[3];
        Assert.Equal("ABC125", replaced.TargetId);
        Assert.Equal(("ABC123", orderId.ToString()), (replaced.Details.GetProperty("oldSerial").GetString(), replaced.Details.GetProperty("refId").GetString()));
        Assert.Equal("ABC123", audits[1].TargetId);
    }

    [Fact]
    public async Task Unassign_of_serial_assigned_elsewhere_is_SERIAL_NOT_AVAILABLE()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);
        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC123"), cancellationToken: Ct);
        var elsewhere = Guid.CreateVersion7().ToString();

        await FailsAsync(t.Packing.UnassignSerialAsync(new() { RefId = elsewhere, Serial = "ABC123" }, cancellationToken: Ct), "SERIAL_NOT_AVAILABLE");
        await FailsAsync(t.Packing.UnassignSerialAsync(new() { RefId = orderId.ToString(), Serial = "NOSUCH1" }, cancellationToken: Ct), "SERIAL_NOT_AVAILABLE");
        await FailsAsync(t.Packing.ReplaceSerialAsync(new() { RefId = elsewhere, OldSerial = "ABC123", NewSerial = "ABC125" }, cancellationToken: Ct), "SERIAL_NOT_AVAILABLE");
        await FailsAsync(t.Packing.ReplaceSerialAsync(new() { RefId = orderId.ToString(), OldSerial = "ABC123", NewSerial = "ABC123" }, cancellationToken: Ct), "SERIAL_NOT_AVAILABLE");
        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC124"), cancellationToken: Ct);
        await FailsAsync(t.Packing.ReplaceSerialAsync(new() { RefId = orderId.ToString(), OldSerial = "ABC123", NewSerial = "ABC124" }, cancellationToken: Ct), "SERIAL_NOT_AVAILABLE");

        Assert.Equal(SerialStatuses.Assigned, (await SerialAsync(t, "ABC123")).Status);
    }

    [Fact]
    public async Task Scanned_serial_is_normalized()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);

        await t.Packing.AssignSerialAsync(Assign(orderId, sku, " abc123 "), cancellationToken: Ct);
        Assert.Equal(SerialStatuses.Assigned, (await SerialAsync(t, "ABC123")).Status);

        var info = await FailsAsync(t.Packing.AssignSerialAsync(Assign(orderId, sku, "ab-1"), cancellationToken: Ct), "VALIDATION_FAILED");
        Assert.Equal("serial", info.Metadata["field"]);
    }

    [Fact]
    public async Task Missing_staff_headers_audit_with_empty_actor()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (sku, orderId) = await CommittedAsync(t);

        await t.Packing.AssignSerialAsync(Assign(orderId, sku, "ABC123"), cancellationToken: Ct);

        var audit = Assert.Single(await SerialAuditAsync(t));
        Assert.Equal((Guid.Empty, ""), (audit.ActorId, audit.ActorRole));
    }
}
