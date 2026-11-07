using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Inventory.Contracts;
using Bloom.Order.Contracts;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Fulfillment.Tests;

public class PackingTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TestCaller Warehouse = TestCaller.Staff("warehouse");

    private static string Tasks(string tail = "") => "/api/admin/fulfillment/tasks" + tail;

    private static async Task<(Guid OrderId, Guid TaskId, OrderReadyToFulfillV1 Ready)> ReadyTaskAsync(TestFulfillment t, Action<Seed.ReadyBuilder>? configure = null)
    {
        var ready = Seed.Ready(configure);
        await Seed.ReadyAsync(t, ready);
        return (ready.OrderId, (await Seed.TaskAsync(t, ready.OrderId))!.Id, ready);
    }

    private static async Task<JsonElement> PostOkAsync(TestFulfillment t, string path, object body, TestCaller? caller = null)
    {
        using var response = await t.SendAsync(HttpMethod.Post, path, body, caller ?? Warehouse);
        return await response.OkJsonAsync();
    }

    private static async Task<(Guid OrderId, Guid TaskId, OrderReadyToFulfillV1 Ready)> PackingTaskAsync(TestFulfillment t, Action<Seed.ReadyBuilder>? configure = null)
    {
        var task = await ReadyTaskAsync(t, configure);
        await PostOkAsync(t, Tasks($"/{task.TaskId}/start"), new { version = 0 });
        return task;
    }

    private static Task<JsonElement> ScanAsync(TestFulfillment t, Guid taskId, int lineNo, string serial) =>
        PostOkAsync(t, Tasks($"/{taskId}/serials"), new { lineNo, serial });

    private static async Task ExpectScanFailsAsync(TestFulfillment t, Guid taskId, int lineNo, string serial, int http, string reason, string? field = null)
    {
        using var response = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/serials"), new { lineNo, serial }, Warehouse);
        var info = await response.ExpectErrorAsync(http, reason);
        if (field is not null) Assert.Equal(field, info.GetProperty("metadata").GetProperty("field").GetString());
    }

    private static IEnumerable<(int Line, string Serial, int Seq)> Serials(JsonElement task) =>
        task.GetProperty("serials").EnumerateArray().Select(s => (s.GetProperty("lineNo").GetInt32(), s.GetProperty("serial").GetString()!, s.GetProperty("seq").GetInt32()));

    private static async Task<List<PackTaskSerial>> RowsAsync(TestFulfillment t, Guid taskId)
    {
        await using var db = t.NewDb();
        return await db.PackTaskSerials.AsNoTracking().Where(s => s.TaskId == taskId).OrderBy(s => s.Seq).ToListAsync(Ct);
    }

    [Fact]
    public async Task Start_packing_publishes_packing_started_and_audits()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderId, taskId, _) = await ReadyTaskAsync(t);
        var staff = TestCaller.Staff("warehouse");

        using (var stale = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/start"), new { version = 7 }, staff))
            await stale.ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        Assert.Equal(TaskStatuses.Pending, (await Seed.TaskAsync(t, orderId))!.Status);
        Assert.Empty(await Outbox.PayloadsAsync<PackingStartedV1>(t, FulfillmentMessages.PackingStarted));

        using var ok = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/start"), new { version = 0 }, staff);
        var task = await ok.OkJsonAsync();

        Assert.Equal(("packing", "1", "order", orderId.ToString(), "standard"),
            (task.GetProperty("status").GetString(), task.GetProperty("version").GetString(), task.GetProperty("kind").GetString(), task.GetProperty("orderId").GetString(), task.GetProperty("shippingOption").GetString()));
        Assert.Equal(2, task.GetProperty("lines").GetArrayLength());
        Assert.Equal(TaskStatuses.Packing, (await Seed.TaskAsync(t, orderId))!.Status);
        Assert.Equal([orderId], (await Outbox.PayloadsAsync<PackingStartedV1>(t, FulfillmentMessages.PackingStarted)).Select(p => p.OrderId));
        var audit = (await Outbox.AuditAsync(t)).Single();
        Assert.Equal((staff.Id, "warehouse", "fulfillment", "fulfillment.start-packing", "pack_task", taskId.ToString()),
            (audit.ActorId, audit.ActorRole, audit.Service, audit.Action, audit.TargetType, audit.TargetId));

        using var again = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/start"), new { version = 1 }, staff);
        await again.ExpectErrorAsync(400, "INVALID_STATE");
        using var unknown = await t.SendAsync(HttpMethod.Post, Tasks($"/{Guid.NewGuid()}/start"), new { version = 0 }, staff);
        await unknown.ExpectErrorAsync(404, "NOT_FOUND");
        using var bad = await t.SendAsync(HttpMethod.Post, Tasks("/nope/start"), new { version = 0 }, staff);
        await bad.ExpectValidationAsync("task_id");
    }

    [Fact]
    public async Task F03_customer_cancel_during_packing_is_PACKING_STARTED()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderId, _, _) = await PackingTaskAsync(t);

        await Rpc.FailsAsync(t.Internal.CancelPackTaskAsync(new() { OrderId = orderId.ToString("D"), By = "customer" }, cancellationToken: Ct), "PACKING_STARTED");

        Assert.Equal(TaskStatuses.Packing, (await Seed.TaskAsync(t, orderId))!.Status);
    }

    [Fact]
    public async Task Support_cancel_during_packing_and_packed()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (packingOrder, _, _) = await PackingTaskAsync(t);
        var packed = Seed.Ready();
        await Seed.ReadyAsync(t, packed);
        var (_, shipmentId) = await Seed.PackedAsync(t, packed, "PK1", "PK2");

        await t.Internal.CancelPackTaskAsync(new() { OrderId = packingOrder.ToString("D"), By = "support" }, cancellationToken: Ct).ResponseAsync;
        await t.Internal.CancelPackTaskAsync(new() { OrderId = packed.OrderId.ToString("D"), By = "support" }, cancellationToken: Ct).ResponseAsync;

        Assert.Equal(TaskStatuses.Cancelled, (await Seed.TaskAsync(t, packingOrder))!.Status);
        Assert.Equal(TaskStatuses.Cancelled, (await Seed.TaskAsync(t, packed.OrderId))!.Status);
        await using var db = t.NewDb();
        Assert.Equal(ShipmentStatuses.Voided, (await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipmentId, Ct)).Status); // label_pending: nothing was bought
        var coverage = await db.WarrantyCoverages.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((packed.OrderId, "PK1", CoverageStatuses.Cancelled), (coverage.OrderId, coverage.Serial, coverage.Status));

        var again = Seed.Ready(); // the released serial is free for another order (ux_warranty_coverages_serial_open)
        await Seed.ReadyAsync(t, again);
        await Seed.PackedAsync(t, again, "PK1", "PK3");
        Assert.Equal([CoverageStatuses.Cancelled, CoverageStatuses.PendingDelivery],
            await db.WarrantyCoverages.AsNoTracking().OrderBy(c => c.Status).Select(c => c.Status).ToListAsync(Ct));
    }

    [Fact]
    public async Task Scan_calls_inventory_first_and_records_seq()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderId, taskId, ready) = await PackingTaskAsync(t);
        var staff = TestCaller.Staff("warehouse");
        var rowsWhenInventoryAnswered = -1;
        t.Inventory.Before = async () => rowsWhenInventoryAnswered = (await RowsAsync(t, taskId)).Count;

        using var first = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/serials"), new { lineNo = 1, serial = " abc1 " }, staff);
        var task = await first.OkJsonAsync();
        t.Inventory.Before = null;
        var second = await ScanAsync(t, taskId, 1, "ABC2");

        Assert.Equal([(1, "ABC1", 1)], Serials(task));
        Assert.Equal([(1, "ABC1", 1), (1, "ABC2", 2)], Serials(second));
        Assert.Equal(0, rowsWhenInventoryAnswered);
        var call = t.Inventory.Calls.First();
        Assert.Equal(("AssignSerial", staff.Id.ToString("D")), (call.Method, call.UserId));
        var request = Assert.IsType<AssignSerialRequest>(call.Request);
        Assert.Equal((orderId.ToString(), ready.Lines[0].SkuId.ToString(), "ABC1"), (request.RefId, request.SkuId, request.Serial));
        Assert.Equal(["ABC1", "ABC2"], (await RowsAsync(t, taskId)).Select(r => r.Serial));
        var audit = (await Outbox.AuditAsync(t)).Last();
        Assert.Equal(("fulfillment.scan-serial", taskId.ToString()), (audit.Action, audit.TargetId));
        Assert.Equal("ABC2", audit.Details.GetProperty("serial").GetString());
    }

    [Fact]
    public async Task Inventory_refusal_passes_through_and_writes_nothing()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, taskId, _) = await PackingTaskAsync(t);
        t.Inventory.Fail("AssignSerial", StatusCode.FailedPrecondition, "SERIAL_NOT_AVAILABLE");
        var auditBefore = (await Outbox.AuditAsync(t)).Count;

        await ExpectScanFailsAsync(t, taskId, 1, "ABC1", 400, "SERIAL_NOT_AVAILABLE");

        Assert.Empty(await RowsAsync(t, taskId));
        Assert.Equal(auditBefore, (await Outbox.AuditAsync(t)).Count);
    }

    [Fact]
    public async Task Scan_retry_after_lost_local_write_gives_one_row()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, taskId, _) = await PackingTaskAsync(t);
        await ScanAsync(t, taskId, 1, "ABC1");
        await using (var db = t.NewDb()) await db.PackTaskSerials.Where(s => s.TaskId == taskId).ExecuteDeleteAsync(Ct); // inventory answered ok, the local write was lost

        var retry = await ScanAsync(t, taskId, 1, "ABC1");

        Assert.Equal([(1, "ABC1", 1)], Serials(retry));
        Assert.Equal(2, t.Inventory.Count("AssignSerial"));
        Assert.Single(await RowsAsync(t, taskId));

        var third = await ScanAsync(t, taskId, 1, "abc1");

        Assert.Equal([(1, "ABC1", 1)], Serials(third));
        Assert.Equal(2, t.Inventory.Count("AssignSerial"));
        Assert.Single(await RowsAsync(t, taskId));
    }

    [Fact]
    public async Task Scan_rules()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, taskId, ready) = await PackingTaskAsync(t, b => b.Line(3).Line(4, serialized: true, quantity: 1));
        var pending = await ReadyTaskAsync(t);

        await ExpectScanFailsAsync(t, taskId, 3, "A1", 400, "VALIDATION_FAILED", "line_no"); // not serialized
        await ExpectScanFailsAsync(t, taskId, 2, "A1", 400, "VALIDATION_FAILED", "line_no"); // plan line
        await ExpectScanFailsAsync(t, taskId, 9, "A1", 400, "VALIDATION_FAILED", "line_no"); // no such line
        await ExpectScanFailsAsync(t, taskId, 1, "A-1", 400, "VALIDATION_FAILED", "serial");
        await ScanAsync(t, taskId, 1, "A1");
        await ScanAsync(t, taskId, 1, "A2");
        await ExpectScanFailsAsync(t, taskId, 1, "A3", 400, "VALIDATION_FAILED", "line_no"); // full
        await ExpectScanFailsAsync(t, taskId, 4, "A1", 400, "VALIDATION_FAILED", "serial"); // on line 1
        await ExpectScanFailsAsync(t, pending.TaskId, 1, "B1", 400, "INVALID_STATE");

        Assert.Equal(2, t.Inventory.Count("AssignSerial"));
        Assert.Equal(["A1", "A2"], (await RowsAsync(t, taskId)).Select(r => r.Serial));
        Assert.Empty(await RowsAsync(t, pending.TaskId));
        Assert.Equal(ready.Lines[0].SkuId.ToString(), ((AssignSerialRequest)t.Inventory.Calls[0].Request).SkuId);
    }

    [Fact]
    public async Task Unassign_and_replace_keep_positions()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderId, taskId, _) = await PackingTaskAsync(t);
        await ScanAsync(t, taskId, 1, "A1");
        await ScanAsync(t, taskId, 1, "A2");

        var replaced = await PostOkAsync(t, Tasks($"/{taskId}/serials/replace"), new { oldSerial = "a1", newSerial = "B1" });

        Assert.Equal([(1, "B1", 1), (1, "A2", 2)], Serials(replaced));
        var call = Assert.IsType<ReplaceSerialRequest>(t.Inventory.Calls.Last().Request);
        Assert.Equal((orderId.ToString(), "A1", "B1"), (call.RefId, call.OldSerial, call.NewSerial));

        var again = await PostOkAsync(t, Tasks($"/{taskId}/serials/replace"), new { oldSerial = "A1", newSerial = "B1" });
        Assert.Equal([(1, "B1", 1), (1, "A2", 2)], Serials(again));
        Assert.Equal(1, t.Inventory.Count("ReplaceSerial"));

        using (var missing = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/serials/replace"), new { oldSerial = "Z9", newSerial = "Y9" }, Warehouse))
            await missing.ExpectValidationAsync("old_serial");
        using (var taken = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/serials/replace"), new { oldSerial = "B1", newSerial = "A2" }, Warehouse))
            await taken.ExpectValidationAsync("new_serial");
        Assert.Equal(1, t.Inventory.Count("ReplaceSerial"));

        var unknown = await PostOkAsync(t, Tasks($"/{taskId}/serials/unassign"), new { serial = "Q1" });
        Assert.Equal([(1, "B1", 1), (1, "A2", 2)], Serials(unknown));
        Assert.Equal(0, t.Inventory.Count("UnassignSerial"));

        var unassigned = await PostOkAsync(t, Tasks($"/{taskId}/serials/unassign"), new { serial = "a2" });
        Assert.Equal([(1, "B1", 1)], Serials(unassigned));
        var un = Assert.IsType<UnassignSerialRequest>(t.Inventory.Calls.Last().Request);
        Assert.Equal((orderId.ToString(), "A2"), (un.RefId, un.Serial));
        Assert.Equal(["fulfillment.scan-serial", "fulfillment.scan-serial", "fulfillment.replace-serial", "fulfillment.unassign-serial"],
            (await Outbox.AuditAsync(t)).Skip(1).Select(a => a.Action));

        var pending = await ReadyTaskAsync(t);
        using var notPacking = await t.SendAsync(HttpMethod.Post, Tasks($"/{pending.TaskId}/serials/unassign"), new { serial = "B1" }, Warehouse);
        await notPacking.ExpectErrorAsync(400, "INVALID_STATE");
    }

    [Fact]
    public async Task F04_complete_packing_with_a_missing_serial_is_SERIALS_INCOMPLETE()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderId, taskId, _) = await PackingTaskAsync(t);
        await ScanAsync(t, taskId, 1, "A1");

        using var response = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/complete"), new { version = 1 }, Warehouse);

        await response.ExpectErrorAsync(400, "SERIALS_INCOMPLETE");
        Assert.Equal(TaskStatuses.Packing, (await Seed.TaskAsync(t, orderId))!.Status);
        await using var db = t.NewDb();
        Assert.False(await db.Shipments.AnyAsync(Ct));
        Assert.False(await db.WarrantyCoverages.AnyAsync(Ct));
    }

    [Fact]
    public async Task F05_one_plan_for_a_two_unit_device_covers_the_first_scanned_serial()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderId, taskId, _) = await PackingTaskAsync(t, b => b.Express(1500));
        var staff = TestCaller.Staff("admin");
        await ScanAsync(t, taskId, 1, "B");
        await ScanAsync(t, taskId, 1, "A");

        using (var stale = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/complete"), new { version = 0 }, staff))
            await stale.ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        using var response = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/complete"), new { version = 1 }, staff);
        var body = await response.OkJsonAsync();

        var task = body.GetProperty("task");
        var shipment = body.GetProperty("shipment");
        Assert.Equal(("packed", "2"), (task.GetProperty("status").GetString(), task.GetProperty("version").GetString()));
        Assert.Equal(("outbound", "express", "label_pending"),
            (shipment.GetProperty("direction").GetString(), shipment.GetProperty("service").GetString(), shipment.GetProperty("status").GetString()));
        await using var db = t.NewDb();
        var row = await db.Shipments.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((taskId, orderId, "outbound", "express", "label_pending", t.Clock.GetUtcNow(), t.Clock.GetUtcNow(), 0, shipment.GetProperty("id").GetString()),
            (row.PackTaskId, row.OrderId, row.Direction, row.Service, row.Status, row.CreatedAt, row.NextAttemptAt, row.LabelAttempts, row.Id.ToString()));
        var coverage = await db.WarrantyCoverages.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((orderId, 2, 1, "B", 12, "pending_delivery", (DateTimeOffset?)null),
            (coverage.OrderId, coverage.PlanLineNo, coverage.DeviceLineNo, coverage.Serial, coverage.CoverageMonths, coverage.Status, coverage.StartsAt));
        var audit = (await Outbox.AuditAsync(t)).Last();
        Assert.Equal(("admin", "fulfillment.complete-packing", taskId.ToString()), (audit.ActorRole, audit.Action, audit.TargetId));

        using var twice = await t.SendAsync(HttpMethod.Post, Tasks($"/{taskId}/complete"), new { version = 2 }, staff);
        await twice.ExpectErrorAsync(400, "INVALID_STATE");
        Assert.Equal(1, await db.Shipments.CountAsync(Ct));
    }

    [Fact]
    public async Task List_pack_tasks_filters_and_pages()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await ReadyTaskAsync(t)).TaskId);
            t.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        await PostOkAsync(t, Tasks($"/{ids[1]}/start"), new { version = 0 });
        await t.Internal.CancelPackTaskAsync(new() { OrderId = Guid.NewGuid().ToString("D"), By = "customer" }, cancellationToken: Ct).ResponseAsync; // a marker

        async Task<List<string?>> ListAsync(string query)
        {
            using var response = await t.SendAsync(HttpMethod.Get, Tasks(query), caller: Warehouse);
            return (await response.OkJsonAsync()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        }

        Assert.Equal(ids.Select(i => i.ToString()), await ListAsync(""));
        Assert.Equal([ids[0].ToString(), ids[2].ToString()], await ListAsync("?status=pending"));
        Assert.Equal([ids[1].ToString()], await ListAsync("?status=packing"));
        Assert.Equal([ids[2].ToString()], await ListAsync("?status=pending&pageSize=1&page=2"));
        Assert.Empty(await ListAsync("?status=cancelled"));
        using var bad = await t.SendAsync(HttpMethod.Get, Tasks("?status=nope"), caller: Warehouse);
        await bad.ExpectValidationAsync("status");
        using var badPage = await t.SendAsync(HttpMethod.Get, Tasks("?pageSize=101"), caller: Warehouse);
        await badPage.ExpectValidationAsync("page_size");
    }

    [Fact]
    public async Task Commit_for_reship_sends_the_lines_and_maps_sold_out_to_RESHIP_NO_STOCK()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        await using var scope = t.Services.CreateAsyncScope();
        var calls = scope.ServiceProvider.GetRequiredService<InventoryCalls>();
        var (reship, order, sku) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var support = TestCaller.Staff("support");
        var staff = new FakeCallContext(support);

        await calls.CommitForReshipAsync(reship, order, [(sku, 2)], staff);

        var request = Assert.IsType<CommitForReshipRequest>(t.Inventory.Calls.Single().Request);
        Assert.Equal((reship.ToString(), order.ToString(), sku.ToString(), 2), (request.ReshipId, request.OrderId, request.Lines.Single().SkuId, request.Lines.Single().Quantity));
        Assert.Equal(support.Id.ToString(), t.Inventory.Calls.Single().UserId); // R11: the staff caller's headers travel

        t.Inventory.SoldOut = true;
        var e = await Assert.ThrowsAsync<RpcException>(() => calls.CommitForReshipAsync(reship, order, [(sku, 2)], staff));
        Assert.Equal("RESHIP_NO_STOCK", Rpc.Info(e).Reason);

        t.Inventory.SoldOut = false;
        t.Inventory.Fail("CommitForReship", StatusCode.FailedPrecondition, "NOT_COMMITTED");
        e = await Assert.ThrowsAsync<RpcException>(() => calls.CommitForReshipAsync(reship, order, [(sku, 2)], staff));
        Assert.Equal("NOT_COMMITTED", Rpc.Info(e).Reason);
    }

    [Theory, InlineData("support"), InlineData("customer")]
    public async Task Only_warehouse_and_admin_pack(string role)
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, taskId, _) = await ReadyTaskAsync(t);
        var caller = role == "customer" ? TestCaller.Customer() : TestCaller.Staff(role);

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, Tasks(), null),
            (HttpMethod.Post, Tasks($"/{taskId}/start"), new { version = 0 }),
            (HttpMethod.Post, Tasks($"/{taskId}/serials"), new { lineNo = 1, serial = "A1" }),
            (HttpMethod.Post, Tasks($"/{taskId}/serials/unassign"), new { serial = "A1" }),
            (HttpMethod.Post, Tasks($"/{taskId}/serials/replace"), new { oldSerial = "A1", newSerial = "A2" }),
            (HttpMethod.Post, Tasks($"/{taskId}/complete"), new { version = 0 }),
        })
        {
            using var response = await t.SendAsync(method, path, body, caller);
            await response.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        }

        Assert.Equal(TaskStatuses.Pending, (await Seed.TaskAsync(t, (await ReadyTaskAsync(t)).OrderId))!.Status);
        Assert.Empty(t.Inventory.Calls);
    }
}
