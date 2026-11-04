using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Consumers;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class AdjustmentTests(Containers containers)
{
    private const string Path = "/api/admin/inventory/adjustments";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> AdjustAsync(TestInventory t, Guid sku, int delta, string reason, string note = "counted",
        string? serial = null, string[]? newSerials = null, string[]? newImeis = null, TestCaller? caller = null, Guid? key = null) =>
        t.SendAsync(HttpMethod.Post, Path,
            new { skuId = sku, delta, reasonCode = reason, note, serial = serial ?? "", newSerials = newSerials ?? [], newImeis = newImeis ?? [] },
            caller ?? TestCaller.Staff("warehouse"), key ?? Guid.NewGuid());

    private static async Task<Guid> AdjustedAsync(HttpResponseMessage response) =>
        Guid.Parse((await response.OkJsonAsync()).GetProperty("adjustmentId").GetString()!);

    private static async Task ExpectValidationAsync(HttpResponseMessage response, string field)
    {
        var info = await response.ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal(field, info.GetProperty("metadata").GetProperty("field").GetString());
    }

    /// <summary>A 15-digit IMEI with a valid Luhn check digit, built from <paramref name="n"/>.</summary>
    private static string Imei(int n)
    {
        var body = $"35{n:D12}";
        var sum = body.Reverse().Select((c, i) => (c - '0') * (i % 2 == 0 ? 2 : 1)).Sum(d => d / 10 + d % 10);
        return body + (10 - sum % 10) % 10;
    }

    private static Task ReserveAsync(TestInventory t, Guid orderId, Guid sku, int quantity) =>
        t.Stock.ReserveStockAsync(new() { OrderId = orderId.ToString(), Lines = { new StockLine { SkuId = sku.ToString(), Quantity = quantity } } }, cancellationToken: Ct).ResponseAsync;

    private static Task PaidAsync(TestInventory t, Guid orderId, Guid? launchId, Guid sku, int quantity) =>
        Consume.Async<OrderPaidConsumer>(t, OrderMessages.Paid,
            new OrderPaidV1(orderId, "BL-1", Guid.CreateVersion7(), launchId, DateTimeOffset.UtcNow, 1000, [new OrderPaidLineV1(sku, quantity)]));

    private static Task ReleaseAsync(TestInventory t, Guid orderId) =>
        Consume.Async<ReleaseReservationConsumer>(t, InventoryMessages.ReleaseReservation, new ReleaseReservationV1(orderId));

    private static Task ShipAsync(TestInventory t, Guid orderId, Guid sku, int quantity) =>
        Consume.Async<ShipmentShippedConsumer>(t, FulfillmentMessages.ShipmentShipped,
            new ShipmentShippedV1(orderId, orderId, [new ShippedLineV1(sku, quantity, [])], "TRK1"));

    private static (int OnHand, int Reserved, int Committed, int Returned, int Damaged, int Lost) Counters(StockItem i) =>
        (i.OnHand, i.Reserved, i.Committed, i.Returned, i.Damaged, i.Lost);

    private static async Task<SerialUnit> SerialAsync(TestInventory t, string serial)
    {
        await using var db = t.NewDb();
        return await db.SerialUnits.AsNoTracking().SingleAsync(s => s.Serial == serial, Ct);
    }

    private static async Task SetSerialStatusAsync(TestInventory t, string serial, string status)
    {
        await using var db = t.NewDb();
        await db.Database.ExecuteSqlAsync($"UPDATE serial_units SET status = {status}, ref_id = {(status == SerialStatuses.Assigned ? Guid.NewGuid() : null)} WHERE serial = {serial}", Ct);
    }

    private static async Task<int> AdjustmentRowsAsync(TestInventory t)
    {
        await using var db = t.NewDb();
        return await db.StockAdjustments.CountAsync(Ct);
    }

    [Fact]
    public async Task I15_decrease_larger_than_available_is_ADJUSTMENT_EXCEEDS_AVAILABLE()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 3);
        await ReserveAsync(t, Guid.CreateVersion7(), sku, 2);
        var ledgerBefore = (await Seed.LedgerAsync(t, sku)).Count;
        var auditBefore = (await Outbox.AuditAsync(t)).Count;

        using var response = await AdjustAsync(t, sku, -2, ReasonCodes.CountCorrection);

        await response.ExpectErrorAsync(400, "ADJUSTMENT_EXCEEDS_AVAILABLE");
        Assert.Equal((3, 2, 0, 0, 0, 0), Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Equal(0, await AdjustmentRowsAsync(t));
        Assert.Equal(ledgerBefore, (await Seed.LedgerAsync(t, sku)).Count);
        Assert.Equal(auditBefore, (await Outbox.AuditAsync(t)).Count);
    }

    [Fact]
    public async Task Damaged_unit_promised_to_an_order_is_refused()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 1);
        await ReserveAsync(t, Guid.CreateVersion7(), sku, 1);

        using var response = await AdjustAsync(t, sku, -1, ReasonCodes.DamagedInWarehouse);

        await response.ExpectErrorAsync(400, "ADJUSTMENT_EXCEEDS_AVAILABLE");
        Assert.Equal((1, 1, 0, 0, 0, 0), Counters((await Seed.ItemAsync(t, sku))!));
    }

    [Fact]
    public async Task Damaged_decrease_moves_units_to_damaged()
    {
        await using var t = await TestInventory.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var staff = TestCaller.Staff("admin");

        using var response = await AdjustAsync(t, sku, -2, ReasonCodes.DamagedInWarehouse, "  dropped pallet  ", caller: staff);
        var id = await AdjustedAsync(response);

        Assert.Equal((3, 0, 0, 0, 2, 0), Counters((await Seed.ItemAsync(t, sku))!));
        await using var db = t.NewDb();
        var row = await db.StockAdjustments.SingleAsync(Ct);
        Assert.Equal((id, sku, null, -2, "damaged_in_warehouse", "dropped pallet", staff.Id),
            (row.Id, row.SkuId, row.Serial, row.Delta, row.ReasonCode, row.Note, row.StaffId));
        var entry = (await Seed.LedgerAsync(t, sku)).Last();
        Assert.Equal((EntryTypes.Adjusted, -2, 2, 0, RefTypes.Adjustment, id, staff.Id.ToString("D")),
            (entry.EntryType, entry.DOnHand, entry.DDamaged, entry.DLost, entry.RefType, entry.RefId, entry.Actor));
        var audit = (await Outbox.AuditAsync(t)).Last();
        Assert.Equal((staff.Id, "admin", "inventory", "inventory.stock-adjusted", "adjustment", id.ToString()),
            (audit.ActorId, audit.ActorRole, audit.Service, audit.Action, audit.TargetType, audit.TargetId));
        Assert.Equal((sku.ToString(), -2, "damaged_in_warehouse"),
            (audit.Details.GetProperty("skuId").GetString(), audit.Details.GetProperty("delta").GetInt32(), audit.Details.GetProperty("reasonCode").GetString()));
        Assert.Equal(1, metrics.Sum("adjustments_total", ("reason_code", "damaged_in_warehouse")));
    }

    [Fact]
    public async Task Lost_in_warehouse_moves_units_to_lost()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);

        using var response = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse);
        await AdjustedAsync(response);

        Assert.Equal((4, 0, 0, 0, 0, 1), Counters((await Seed.ItemAsync(t, sku))!));
    }

    [Fact]
    public async Task Count_correction_decrease_changes_only_on_hand()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);

        using var response = await AdjustAsync(t, sku, -2, ReasonCodes.CountCorrection);
        await AdjustedAsync(response);

        Assert.Equal((3, 0, 0, 0, 0, 0), Counters((await Seed.ItemAsync(t, sku))!));
    }

    [Fact]
    public async Task Increase_runs_the_allocator()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 1);
        var waiting = await Seed.BackorderAsync(t, sku, 2, t.Clock.GetUtcNow().AddHours(-1));

        using var response = await AdjustAsync(t, sku, 2, ReasonCodes.Found);
        var id = await AdjustedAsync(response);

        Assert.Equal((1, 0, 2, 0, 0, 0), Counters((await Seed.ItemAsync(t, sku))!));
        await using var db = t.NewDb();
        Assert.Equal(AllocationStatuses.Committed, (await db.StockAllocations.SingleAsync(a => a.OrderId == waiting, Ct)).Status);
        var ledger = await Seed.LedgerAsync(t, sku);
        Assert.Equal([EntryTypes.Adjusted, EntryTypes.BackorderAllocated], ledger.TakeLast(2).Select(e => e.EntryType));
        Assert.Equal((2, id), (ledger[^2].DOnHand, ledger[^2].RefId));
        Assert.Equal([waiting], (await Outbox.PayloadsAsync<StockAllocatedV1>(t, InventoryMessages.StockAllocated)).Select(m => m.OrderId));
    }

    [Fact]
    public async Task Reason_must_fit_the_direction()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);

        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.Found)) await ExpectValidationAsync(r, "reason_code");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.DamagedInWarehouse)) await ExpectValidationAsync(r, "reason_code");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.LostInWarehouse)) await ExpectValidationAsync(r, "reason_code");
        using (var r = await AdjustAsync(t, sku, 1, "miscounted")) await ExpectValidationAsync(r, "reason_code");
        using (var r = await AdjustAsync(t, sku, 0, ReasonCodes.Other)) await ExpectValidationAsync(r, "delta");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Other, note: "")) await ExpectValidationAsync(r, "note");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Other, note: "   ")) await ExpectValidationAsync(r, "note");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Other, note: new string('x', 501))) await ExpectValidationAsync(r, "note");
        using (var r = await t.SendAsync(HttpMethod.Post, Path, new { skuId = "nope", delta = 1, reasonCode = "found", note = "n" }, TestCaller.Staff("warehouse"), Guid.NewGuid()))
            await ExpectValidationAsync(r, "sku_id");

        Assert.Equal(5, (await Seed.ItemAsync(t, sku))!.OnHand);
        Assert.Equal(0, await AdjustmentRowsAsync(t));
        using var ok = await AdjustAsync(t, sku, 1, ReasonCodes.Other, note: new string('x', 500));
        await AdjustedAsync(ok);
    }

    [Fact]
    public async Task Roles_and_unknown_skus_are_refused()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var unstocked = Seed.Sku(t);
        var key = Guid.NewGuid();

        using var denied = await AdjustAsync(t, unstocked, 1, ReasonCodes.Found, caller: TestCaller.Staff("support"), key: key);
        await denied.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        using var none = await AdjustAsync(t, unstocked, 1, ReasonCodes.Found);
        await none.ExpectErrorAsync(400, "SKU_NOT_STOCKED");
        using var missingKey = await t.SendAsync(HttpMethod.Post, Path, new { skuId = unstocked, delta = 1, reasonCode = "found", note = "n" }, TestCaller.Staff("warehouse"));
        await missingKey.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REQUIRED");
    }

    [Fact]
    public async Task Serialized_decrease_names_one_in_stock_serial()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, sku, 5, ["S1", "S2", "S3", "S4", "S5"]);
        await SetSerialStatusAsync(t, "S2", SerialStatuses.Assigned);
        await SetSerialStatusAsync(t, "S3", SerialStatuses.Shipped);

        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "s1"))
        {
            var id = await AdjustedAsync(r);
            Assert.Equal(SerialStatuses.Lost, (await SerialAsync(t, "S1")).Status);
            Assert.Equal((4, 0, 0, 0, 0, 1), Counters((await Seed.ItemAsync(t, sku))!));
            await using var db = t.NewDb();
            Assert.Equal(("S1", id), ((await db.StockAdjustments.SingleAsync(Ct)).Serial, id));
            Assert.Equal("S1", (await Outbox.AuditAsync(t)).Last().Details.GetProperty("serial").GetString());
        }
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.DamagedInWarehouse, serial: "S4"))
        {
            await AdjustedAsync(r);
            Assert.Equal(SerialStatuses.Damaged, (await SerialAsync(t, "S4")).Status);
            Assert.Equal((3, 0, 0, 0, 1, 1), Counters((await Seed.ItemAsync(t, sku))!));
        }

        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "S2")) await r.ExpectErrorAsync(400, "SERIAL_ASSIGNED");
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "S3")) await r.ExpectErrorAsync(400, "SERIAL_NOT_AVAILABLE");
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "S1")) await r.ExpectErrorAsync(400, "SERIAL_NOT_AVAILABLE");
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "NOPE")) await r.ExpectErrorAsync(400, "SERIAL_NOT_AVAILABLE");
        var other = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, other, 1, ["O1"]);
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "O1")) await r.ExpectErrorAsync(400, "SERIAL_NOT_AVAILABLE");
        Assert.Equal(SerialStatuses.InStock, (await SerialAsync(t, "O1")).Status);

        using (var r = await AdjustAsync(t, sku, -2, ReasonCodes.LostInWarehouse, serial: "S5")) await ExpectValidationAsync(r, "delta");
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse)) await ExpectValidationAsync(r, "serial");
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "bad serial!")) await ExpectValidationAsync(r, "serial");
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "S5", newSerials: ["N1"])) await ExpectValidationAsync(r, "new_serials");
        Assert.Equal(SerialStatuses.InStock, (await SerialAsync(t, "S5")).Status);
        Assert.Equal(2, await AdjustmentRowsAsync(t));
    }

    [Fact]
    public async Task Serialized_count_correction_decrease_is_refused()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, sku, 1, ["S1"]);

        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.CountCorrection, serial: "S1")) await ExpectValidationAsync(r, "reason_code");
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.Other, serial: "S1")) await ExpectValidationAsync(r, "reason_code");

        Assert.Equal(SerialStatuses.InStock, (await SerialAsync(t, "S1")).Status);
        Assert.Equal((1, 0, 0, 0, 0, 0), Counters((await Seed.ItemAsync(t, sku))!));
    }

    [Fact]
    public async Task Serialized_increase_with_new_serial_or_lost_serial()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, sku, 2, ["S1", "S2"]);
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, serial: "S1")) await AdjustedAsync(r);

        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: [" n1 "], newImeis: [Imei(7)]))
        {
            var id = await AdjustedAsync(r);
            var created = await SerialAsync(t, "N1");
            Assert.Equal((SerialStatuses.InStock, id, Imei(7), sku), (created.Status, created.ReceiptId, created.Imei, created.SkuId));
            Assert.Equal((2, 0, 0, 0, 0, 1), Counters((await Seed.ItemAsync(t, sku))!));
        }
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, serial: "S1"))
        {
            await AdjustedAsync(r);
            Assert.Equal(SerialStatuses.InStock, (await SerialAsync(t, "S1")).Status);
            Assert.Equal((3, 0, 0, 0, 0, 0), Counters((await Seed.ItemAsync(t, sku))!));
            var entry = (await Seed.LedgerAsync(t, sku)).Last();
            Assert.Equal((EntryTypes.Adjusted, 1, -1), (entry.EntryType, entry.DOnHand, entry.DLost));
        }

        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, serial: "S2")) await r.ExpectErrorAsync(400, "SERIAL_NOT_AVAILABLE"); // in stock, not lost
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: ["S2"])) await ExpectValidationAsync(r, "new_serials"); // exists
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: ["N2"], newImeis: [Imei(7)])) await ExpectValidationAsync(r, "new_imeis"); // exists
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: ["N2"], newImeis: ["123"])) await ExpectValidationAsync(r, "new_imeis");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: ["N2", "N3"])) await ExpectValidationAsync(r, "new_serials");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: ["!"])) await ExpectValidationAsync(r, "new_serials");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newImeis: [Imei(8)])) await ExpectValidationAsync(r, "new_imeis");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found)) await ExpectValidationAsync(r, "serial");
        using (var r = await AdjustAsync(t, sku, 2, ReasonCodes.Found, newSerials: ["N2"])) await ExpectValidationAsync(r, "delta");
        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.LostInWarehouse, newSerials: ["N2"])) await ExpectValidationAsync(r, "new_serials");
        Assert.Equal((3, 0, 0, 0, 0, 0), Counters((await Seed.ItemAsync(t, sku))!));
    }

    [Fact]
    public async Task Plain_sku_takes_no_serials()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 3);

        using (var r = await AdjustAsync(t, sku, -1, ReasonCodes.Other, serial: "S1")) await ExpectValidationAsync(r, "serial");
        using (var r = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: ["N1"])) await ExpectValidationAsync(r, "new_serials");

        Assert.Equal(3, (await Seed.ItemAsync(t, sku))!.OnHand);
    }

    [Fact]
    public async Task Same_key_replays_the_adjustment()
    {
        await using var t = await TestInventory.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var (staff, key) = (TestCaller.Staff("warehouse"), Guid.NewGuid());

        using var first = await AdjustAsync(t, sku, -2, ReasonCodes.DamagedInWarehouse, caller: staff, key: key);
        var id = await AdjustedAsync(first);
        using var again = await AdjustAsync(t, sku, -2, ReasonCodes.DamagedInWarehouse, caller: staff, key: key);

        Assert.Equal(id, await AdjustedAsync(again));
        Assert.Equal((3, 0, 0, 0, 2, 0), Counters((await Seed.ItemAsync(t, sku))!));
        Assert.Equal(1, await AdjustmentRowsAsync(t));
        Assert.Equal(1, metrics.Sum("adjustments_total"));
        using var different = await AdjustAsync(t, sku, -1, ReasonCodes.DamagedInWarehouse, caller: staff, key: key);
        await different.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REUSED");

        // A refusal replays too, even after the stock changed in between.
        var refusedKey = Guid.NewGuid();
        using var refused = await AdjustAsync(t, sku, -4, ReasonCodes.CountCorrection, caller: staff, key: refusedKey);
        await refused.ExpectErrorAsync(400, "ADJUSTMENT_EXCEEDS_AVAILABLE");
        await Seed.ReceiveAsync(t, sku, 10);
        using var refusedAgain = await AdjustAsync(t, sku, -4, ReasonCodes.CountCorrection, caller: staff, key: refusedKey);
        await refusedAgain.ExpectErrorAsync(400, "ADJUSTMENT_EXCEEDS_AVAILABLE");
        Assert.Equal(13, (await Seed.ItemAsync(t, sku))!.OnHand);
    }

    [Fact]
    public async Task Same_key_replays_a_serialized_increase()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        await Seed.ReceiveAsync(t, sku, 1, ["S1"]);
        var (staff, key) = (TestCaller.Staff("warehouse"), Guid.NewGuid());

        using var first = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: ["N1"], caller: staff, key: key);
        var id = await AdjustedAsync(first);
        using var again = await AdjustAsync(t, sku, 1, ReasonCodes.Found, newSerials: ["N1"], caller: staff, key: key); // N1 exists now; the retry must not trip over it

        Assert.Equal(id, await AdjustedAsync(again));
        Assert.Equal(2, (await Seed.ItemAsync(t, sku))!.OnHand);
    }

    [Fact]
    public async Task Ledger_deltas_sum_to_counters()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (plain, phone) = (Seed.Sku(t), Seed.Sku(t, serialized: true));
        await Seed.ReceiveAsync(t, plain, 20);
        await Seed.ReceiveAsync(t, phone, 2, ["P1", "P2"]);

        var (a, b, c, d) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await ReserveAsync(t, a, plain, 5); // a: reserved, paid, shipped, one returned, two restocked, one damaged
        await PaidAsync(t, a, null, plain, 5);
        await ShipAsync(t, a, plain, 5);
        await Consume.Async<CustomerReturnConsumer>(t, FulfillmentMessages.ReceiveCustomerReturn, new ReceiveCustomerReturnV1(a, [new ReturnedUnitV1(plain, null, 1)]));
        await Consume.Async<ReturnedToSenderConsumer>(t, FulfillmentMessages.ReceiveReturnedToSender, new ReceiveReturnedToSenderV1(a, [new ReturnedToSenderUnitV1(plain, null, 2, "sealed")]));
        await Consume.Async<ReturnedToSenderConsumer>(t, FulfillmentMessages.ReceiveReturnedToSender, new ReceiveReturnedToSenderV1(a, [new ReturnedToSenderUnitV1(plain, null, 1, "failed")]));
        await ReserveAsync(t, b, plain, 3); // b: reserved, released
        await ReleaseAsync(t, b);
        await ReserveAsync(t, c, plain, 2); // c: committed, released
        await PaidAsync(t, c, null, plain, 2);
        await ReleaseAsync(t, c);
        await ReserveAsync(t, d, plain, 4); // d: shipped, lost in transit
        await PaidAsync(t, d, null, plain, 4);
        await ShipAsync(t, d, plain, 4);
        await Consume.Async<LostInTransitConsumer>(t, FulfillmentMessages.MarkLostInTransit, new MarkLostInTransitV1(d));
        await PaidAsync(t, Guid.CreateVersion7(), Guid.CreateVersion7(), plain, 20); // pre-order: 13 on hand, waits

        using (var r = await AdjustAsync(t, plain, 10, ReasonCodes.Found)) await AdjustedAsync(r); // allocator serves the pre-order
        using (var r = await AdjustAsync(t, plain, -2, ReasonCodes.DamagedInWarehouse)) await AdjustedAsync(r);
        using (var r = await AdjustAsync(t, plain, -1, ReasonCodes.LostInWarehouse)) await AdjustedAsync(r);
        using (var r = await AdjustAsync(t, phone, -1, ReasonCodes.LostInWarehouse, serial: "P1")) await AdjustedAsync(r);
        using (var r = await AdjustAsync(t, phone, 1, ReasonCodes.Found, serial: "P1")) await AdjustedAsync(r);
        using (var r = await AdjustAsync(t, phone, 1, ReasonCodes.Found, newSerials: ["P3"])) await AdjustedAsync(r);
        using (var r = await AdjustAsync(t, phone, -1, ReasonCodes.DamagedInWarehouse, serial: "P2")) await AdjustedAsync(r);

        var types = new HashSet<string>();
        foreach (var sku in new[] { plain, phone })
        {
            var ledger = await Seed.LedgerAsync(t, sku);
            types.UnionWith(ledger.Select(e => e.EntryType));
            var item = (await Seed.ItemAsync(t, sku))!;
            Assert.Equal(Counters(item), (ledger.Sum(e => e.DOnHand), ledger.Sum(e => e.DReserved), ledger.Sum(e => e.DCommitted),
                ledger.Sum(e => e.DReturned), ledger.Sum(e => e.DDamaged), ledger.Sum(e => e.DLost)));
        }
        Assert.Equal((0, 0, 20, 1, 3, 5), Counters((await Seed.ItemAsync(t, plain))!));
        Assert.Equal(EntryTypes.All.Order(), types.Order());
    }
}
