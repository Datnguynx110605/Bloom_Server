using System.Net;
using System.Text;
using Bloom.Fulfillment.Consumers;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Worker;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Fulfillment.Tests;

/// <summary>Order-side events and direct row writes for tests.</summary>
public static class Seed
{
    public sealed class ReadyBuilder
    {
        internal readonly List<ReadyLineV1> Lines =
        [
            Make(1, "product", serialized: true, quantity: 2, paid: 2002, tax: 160),
            Make(2, "warranty_plan", quantity: 1, parent: 1, months: 12, paid: 9900, tax: 800),
        ];

        internal string Option = "standard";
        internal long ShippingCents;

        /// <summary>Adds the line, or replaces the default line with the same number.</summary>
        public ReadyBuilder Line(int lineNo, string kind = "product", bool serialized = false, int quantity = 1, int? parent = null, int? months = null, long paid = 1000, long tax = 80)
        {
            Lines.RemoveAll(l => l.LineNo == lineNo);
            Lines.Add(Make(lineNo, kind, serialized, quantity, parent, months, paid, tax));
            return this;
        }

        public ReadyBuilder Express(long cents)
        {
            Option = "express";
            ShippingCents = cents;
            return this;
        }

        private static ReadyLineV1 Make(int lineNo, string kind, bool serialized = false, int quantity = 1, int? parent = null, int? months = null, long paid = 1000, long tax = 80) =>
            new(lineNo, kind, Guid.CreateVersion7(), Guid.CreateVersion7(), serialized, quantity, parent, months, paid, tax);
    }

    /// <summary>A valid <c>order.ready-to-fulfill.v1</c>: a serialized device line 1 (2 units) and its 12-month plan line 2, standard shipping at 0 cents.</summary>
    public static OrderReadyToFulfillV1 Ready(Action<ReadyBuilder>? configure = null)
    {
        var b = new ReadyBuilder();
        configure?.Invoke(b);
        var lines = b.Lines.OrderBy(l => l.LineNo).ToList();
        return new OrderReadyToFulfillV1(
            Guid.NewGuid(), Guid.NewGuid(), "BL-1001", new ShipToV1("Ann Lee", ["1 Main St", "Apt 2"], "Reno", "NV", "89501", "+17755550123"),
            b.Option, b.ShippingCents, lines.Sum(l => l.PaidCents) + b.ShippingCents, lines);
    }

    /// <summary>Consumes the event the way <c>ConsumerHost</c> does.</summary>
    public static Task ReadyAsync(TestFulfillment t, OrderReadyToFulfillV1 ready) =>
        Consume.Async<ReadyToFulfillConsumer>(t, OrderMessages.ReadyToFulfill, ready);

    public static async Task<PackTask?> TaskAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.PackTasks.AsNoTracking().SingleOrDefaultAsync(p => p.RefId == orderId, TestContext.Current.CancellationToken);
    }

    public static async Task<Shipment> ShipmentAsync(TestFulfillment t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == id, TestContext.Current.CancellationToken);
    }

    /// <summary>The shipment's delivery failure, or <c>null</c> when none was flagged.</summary>
    public static async Task<DeliveryFailure?> FailureAsync(TestFulfillment t, Guid shipmentId)
    {
        await using var db = t.NewDb();
        return await db.DeliveryFailures.AsNoTracking().SingleOrDefaultAsync(f => f.ShipmentId == shipmentId, TestContext.Current.CancellationToken);
    }

    /// <summary>The order's warranty coverages by plan line.</summary>
    public static async Task<List<WarrantyCoverage>> CoveragesAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.WarrantyCoverages.AsNoTracking().Where(c => c.OrderId == orderId).OrderBy(c => c.PlanLineNo).ToListAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<ReturnRequest> ReturnRequestAsync(TestFulfillment t, Guid returnId)
    {
        await using var db = t.NewDb();
        return await db.ReturnRequests.AsNoTracking().SingleAsync(r => r.Id == returnId, TestContext.Current.CancellationToken);
    }

    public static async Task<List<ReturnUnit>> UnitsAsync(TestFulfillment t, Guid returnId)
    {
        await using var db = t.NewDb();
        return await db.ReturnUnits.AsNoTracking().Where(u => u.ReturnId == returnId).OrderBy(u => u.UnitNo).ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Starts packing, scans <paramref name="serials"/> onto the serialized product lines in line order (each up to its quantity), completes, all as <c>warehouse</c> over HTTP.</summary>
    public static async Task<(Guid TaskId, Guid ShipmentId)> PackedAsync(TestFulfillment t, OrderReadyToFulfillV1 order, params string[] serials)
    {
        var warehouse = TestCaller.Staff("warehouse");
        var task = (await TaskAsync(t, order.OrderId))!;
        var path = $"/api/admin/fulfillment/tasks/{task.Id}";
        using (var start = await t.SendAsync(HttpMethod.Post, $"{path}/start", new { version = task.Version }, warehouse)) await start.OkJsonAsync();
        var next = 0;
        foreach (var line in order.Lines.Where(l => l is { Kind: "product", Serialized: true }).OrderBy(l => l.LineNo))
            for (var i = 0; i < line.Quantity && next < serials.Length; i++)
                using (var scan = await t.SendAsync(HttpMethod.Post, $"{path}/serials", new { lineNo = line.LineNo, serial = serials[next++] }, warehouse)) await scan.OkJsonAsync();
        using (var complete = await t.SendAsync(HttpMethod.Post, $"{path}/complete", new { version = task.Version + 1 }, warehouse)) await complete.OkJsonAsync();

        await using var db = t.NewDb();
        var shipmentId = await db.Shipments.AsNoTracking().Where(s => s.PackTaskId == task.Id).Select(s => s.Id).SingleAsync(TestContext.Current.CancellationToken);
        return (task.Id, shipmentId);
    }

    /// <summary>Makes every UPDATE of the row fail (a poison row, for the sweeps' per-row isolation). Once per database.</summary>
    public static async Task PoisonAsync(TestFulfillment t, string table, Guid id)
    {
        await using var db = t.NewDb();
        var sql = $"""
            CREATE FUNCTION poison() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'poisoned row'; END $$;
            CREATE TRIGGER poison BEFORE UPDATE ON {table} FOR EACH ROW WHEN (OLD.id = '{id}') EXECUTE FUNCTION poison();
            """; // test-only constants: a table name and a Guid
        await db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);
    }

    /// <summary>One pass of the label worker (the API-mode test host doesn't run it, plan R23).</summary>
    public static Task RunLabelWorkerAsync(TestFulfillment t) =>
        ActivatorUtilities.CreateInstance<LabelWorker>(t.Services).RunOnceAsync(TestContext.Current.CancellationToken);

    /// <summary><see cref="PackedAsync"/> plus one label worker pass: the outbound shipment is <c>label_ready</c>.</summary>
    public static async Task<(Guid TaskId, Guid ShipmentId)> LabelReadyAsync(TestFulfillment t, OrderReadyToFulfillV1 order, params string[] serials)
    {
        var packed = await PackedAsync(t, order, serials);
        await RunLabelWorkerAsync(t);
        return packed;
    }

    /// <summary>Shippo now reports <paramref name="status"/> for the number and the webhook fires; asserts HTTP 200.</summary>
    public static async Task TrackAsync(TestFulfillment t, string tracking, string status)
    {
        t.Shippo.SetTracking(tracking, status);
        var body = $$$"""{"event":"track_updated","data":{"tracking_number":"{{{tracking}}}"}}""";
        using var response = await t.Http.PostAsync("/webhooks/shippo/secret-test", new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary><see cref="LabelReadyAsync"/>, the handover scan and a <c>DELIVERED</c> event: the order's return window opens at the fake clock's now.</summary>
    public static async Task<(Guid TaskId, Guid ShipmentId)> DeliveredAsync(TestFulfillment t, OrderReadyToFulfillV1 order, params string[] serials)
    {
        var ready = await LabelReadyAsync(t, order, serials);
        using (var handover = await t.SendAsync(HttpMethod.Post, "/api/admin/fulfillment/handover", new { shipmentId = ready.ShipmentId }, TestCaller.Staff("warehouse")))
            await handover.OkJsonAsync();
        await using var db = t.NewDb();
        var tracking = await db.Shipments.AsNoTracking().Where(s => s.Id == ready.ShipmentId).Select(s => s.TrackingNumber).SingleAsync(TestContext.Current.CancellationToken);
        await TrackAsync(t, tracking!, "DELIVERED");
        return ready;
    }

    /// <summary><c>RequestReturn</c> as <paramref name="customer"/> (status <c>requested</c>, label not yet bought).</summary>
    public static async Task<Guid> ReturnAsync(TestFulfillment t, Guid orderId, TestCaller customer, params (int LineNo, int Qty)[] units)
    {
        using var response = await t.SendAsync(HttpMethod.Post, "/api/returns", new { orderId, units = units.Select(u => new { lineNo = u.LineNo, quantity = u.Qty }) }, customer, Guid.NewGuid());
        return Guid.Parse((await response.OkJsonAsync()).GetProperty("id").GetString()!);
    }

    /// <summary><see cref="ReturnAsync"/>, one label worker pass and <c>ReceiveReturn</c> as <c>warehouse</c>.</summary>
    public static async Task<Guid> ReceivedReturnAsync(TestFulfillment t, Guid orderId, TestCaller customer, params (int LineNo, int Qty)[] units)
    {
        var returnId = await ReturnAsync(t, orderId, customer, units);
        await RunLabelWorkerAsync(t);
        using var receive = await t.SendAsync(HttpMethod.Post, $"/api/admin/fulfillment/returns/{returnId}/receive", new { }, TestCaller.Staff("warehouse"));
        await receive.OkJsonAsync();
        return returnId;
    }

    /// <summary><see cref="ReceivedReturnAsync"/> and <c>RecordInspection</c>, every unit <c>ok</c>. A serialized line's units take the order's shipped serials of that line by <c>seq</c>, skipping serials an earlier return of the order already took.</summary>
    public static async Task<Guid> InspectedReturnAsync(TestFulfillment t, Guid orderId, TestCaller customer, params (int LineNo, int Qty)[] units)
    {
        var returnId = await ReceivedReturnAsync(t, orderId, customer, units);
        var ct = TestContext.Current.CancellationToken;
        await using var db = t.NewDb();
        var rows = await db.ReturnUnits.AsNoTracking().Where(u => u.ReturnId == returnId).OrderBy(u => u.UnitNo).ToListAsync(ct);
        var used = await db.ReturnUnits.AsNoTracking().Where(u => u.ReceivedSerial != null && db.ReturnRequests.Any(r => r.Id == u.ReturnId && r.OrderId == orderId))
            .Select(u => u.ReceivedSerial!).ToListAsync(ct);
        var shipped = (await db.PackTaskSerials.AsNoTracking().Where(s => db.PackTasks.Any(p => p.Id == s.TaskId && p.OrderId == orderId)).OrderBy(s => s.Seq).ToListAsync(ct))
            .GroupBy(s => s.LineNo).ToDictionary(g => g.Key, g => new Queue<string>(g.Select(s => s.Serial).Except(used)));

        var inspection = rows.Select(u => new
        {
            unitNo = u.UnitNo, receivedSerial = shipped.TryGetValue(u.LineNo, out var queue) && queue.Count > 0 ? queue.Dequeue() : "", result = "ok",
        }).ToList();
        using var response = await t.SendAsync(HttpMethod.Post, $"/api/admin/fulfillment/returns/{returnId}/inspection", new { units = inspection }, TestCaller.Staff("warehouse"));
        await response.OkJsonAsync();
        return returnId;
    }

    public static async Task SetTaskStatusAsync(TestFulfillment t, Guid orderId, string status)
    {
        await using var db = t.NewDb();
        await db.PackTasks.Where(p => p.RefId == orderId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, status), TestContext.Current.CancellationToken);
    }
}
