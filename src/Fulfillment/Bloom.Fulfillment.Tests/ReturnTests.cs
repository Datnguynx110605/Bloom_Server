using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Worker;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Fulfillment.Tests;

public class ReturnTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TestCaller Warehouse = TestCaller.Staff("warehouse");
    private const string Admin = "/api/admin/fulfillment/returns";

    /// <summary>A delivered order (device line 1 with two serials, plan line 2) and the serials shipped, in scan order. Each order gets fresh serials: an open coverage keeps its serial.</summary>
    private static async Task<(OrderReadyToFulfillV1 Order, string[] Serials, TestCaller Customer)> DeliveredAsync(TestFulfillment t, Action<Seed.ReadyBuilder>? configure = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var ready = Seed.Ready(configure);
        await Seed.ReadyAsync(t, ready);
        string[] serials = [$"SNB{id}", $"SNA{id}"];
        await Seed.DeliveredAsync(t, ready, serials);
        return (ready, serials, TestCaller.Customer(ready.AccountId));
    }

    private static Task<HttpResponseMessage> RequestAsync(TestFulfillment t, Guid orderId, TestCaller caller, params (int Line, int Qty)[] units) =>
        t.SendAsync(HttpMethod.Post, "/api/returns", new { orderId, units = units.Select(u => new { lineNo = u.Line, quantity = u.Qty }) }, caller);

    private static async Task<Shipment> ReturnShipmentAsync(TestFulfillment t, Guid returnId) =>
        await Seed.ShipmentAsync(t, (await Seed.ReturnRequestAsync(t, returnId)).ReturnShipmentId!.Value);

    private static Task SweepAsync(TestFulfillment t) => ActivatorUtilities.CreateInstance<Sweeps>(t.Services).RunReturnExpiryAsync(Ct);

    private static Task<HttpResponseMessage> ReceiveAsync(TestFulfillment t, Guid returnId, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/{returnId}/receive", new { }, caller ?? Warehouse);

    private static Task<HttpResponseMessage> InspectAsync(TestFulfillment t, Guid returnId, object units, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/{returnId}/inspection", new { units }, caller ?? Warehouse);

    private static object Inspect(int unitNo, string serial = "", string result = "ok", string notes = "") => new { unitNo, receivedSerial = serial, result, notes };

    [Fact]
    public async Task Request_creates_units_and_a_return_label()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);
        var requestedAt = t.Clock.GetUtcNow();

        using var response = await RequestAsync(t, order.OrderId, customer, (1, 2));
        var body = await response.OkJsonAsync();

        var returnId = Guid.Parse(body.GetProperty("id").GetString()!);
        Assert.Equal((order.OrderId.ToString(), "requested"), (body.GetProperty("orderId").GetString(), body.GetProperty("status").GetString()));
        Assert.Equal(requestedAt.AddDays(14), body.GetProperty("expiresAt").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        Assert.Equal([(1, 1), (2, 1)], body.GetProperty("units").EnumerateArray().Select(u => (u.GetProperty("unitNo").GetInt32(), u.GetProperty("lineNo").GetInt32())).ToArray());
        var returnShipment = await ReturnShipmentAsync(t, returnId);
        Assert.Equal((Directions.Return, ShippingOptions.Standard, ShipmentStatuses.LabelPending, returnId, (Guid?)null),
            (returnShipment.Direction, returnShipment.Service, returnShipment.Status, returnShipment.ReturnRequestId, returnShipment.PackTaskId));
        Assert.Equal(ReturnStatuses.Requested, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        Assert.Equal(order.AccountId, (await Seed.ReturnRequestAsync(t, returnId)).AccountId);
        var requested = Assert.Single(await Outbox.PayloadsAsync<ReturnRequestedV1>(t, FulfillmentMessages.ReturnRequested));
        Assert.Equal((order.OrderId, returnId), (requested.OrderId, requested.ReturnId));

        await Seed.RunLabelWorkerAsync(t);

        var issued = await ReturnShipmentAsync(t, returnId);
        Assert.Equal(ShipmentStatuses.LabelReady, issued.Status);
        Assert.Equal(ReturnStatuses.LabelIssued, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        var sent = t.Shippo.ShipmentRequests.Last(); // the outbound label was bought first
        Assert.Equal("Ann Lee", sent.GetProperty("address_from").GetProperty("name").GetString());
        Assert.Equal("Bloom Warehouse", sent.GetProperty("address_to").GetProperty("name").GetString());
        Assert.True(sent.GetProperty("extra").GetProperty("is_return").GetBoolean());
        Assert.Equal("usps_ground_advantage", t.Shippo.BoughtTokens.Last());
        var label = Assert.Single(await Outbox.PayloadsAsync<ReturnLabelIssuedV1>(t, FulfillmentMessages.ReturnLabelIssued));
        Assert.Equal((order.OrderId, returnId, issued.LabelUrl), (label.OrderId, label.ReturnId, label.LabelUrl));
        Assert.StartsWith("https://labels.test/", label.LabelUrl);
    }

    [Fact]
    public async Task Window_and_ownership()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var notDelivered = Seed.Ready();
        await Seed.ReadyAsync(t, notDelivered);
        using (var response = await RequestAsync(t, notDelivered.OrderId, TestCaller.Customer(notDelivered.AccountId), (1, 1)))
            await response.ExpectErrorAsync(400, "RETURN_WINDOW_CLOSED");

        var (order, _, customer) = await DeliveredAsync(t);
        using var other = await RequestAsync(t, order.OrderId, TestCaller.Customer(), (1, 1)); // Review Focus 4
        using var unknown = await RequestAsync(t, Guid.NewGuid(), customer, (1, 1));
        await other.ExpectErrorAsync(404, "NOT_FOUND");
        await unknown.ExpectErrorAsync(404, "NOT_FOUND");
        Assert.Equal(await unknown.Content.ReadAsStringAsync(Ct), await other.Content.ReadAsStringAsync(Ct));
        using (var staff = await RequestAsync(t, order.OrderId, Warehouse, (1, 1)))
            await staff.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");

        t.Clock.Advance(TimeSpan.FromDays(14)); // RET-R01: now <= delivered_at + 14 days
        using (var last = await RequestAsync(t, order.OrderId, customer, (1, 1))) await last.OkJsonAsync();
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        using (var late = await RequestAsync(t, order.OrderId, customer, (1, 1)))
            await late.ExpectErrorAsync(400, "RETURN_WINDOW_CLOSED");
    }

    [Fact]
    public async Task Quantity_counts_open_requests()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);
        var first = await Seed.ReturnAsync(t, order.OrderId, customer, (1, 2));

        using (var more = await RequestAsync(t, order.OrderId, customer, (1, 1)))
            await more.ExpectErrorAsync(400, "RETURN_QUANTITY_EXCEEDED");
        using (var tooMany = await RequestAsync(t, order.OrderId, TestCaller.Customer(order.AccountId), (1, 3)))
            await tooMany.ExpectErrorAsync(400, "RETURN_QUANTITY_EXCEEDED");

        await Seed.RunLabelWorkerAsync(t);
        t.Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromHours(1));
        await SweepAsync(t);
        Assert.Equal(ReturnStatuses.Expired, (await Seed.ReturnRequestAsync(t, first)).Status);

        // The window is closed by now; a reship delivery restarts it (spec §7), which the test does directly.
        await using (var db = t.NewDb())
            await db.OrderAccounts.Where(a => a.OrderId == order.OrderId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeliveredAt, t.Clock.GetUtcNow()), Ct);
        using var again = await RequestAsync(t, order.OrderId, customer, (1, 2));
        await again.OkJsonAsync();
    }

    [Fact]
    public async Task Mixed_plan_and_product_is_VALIDATION_FAILED()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);

        using var response = await RequestAsync(t, order.OrderId, customer, (1, 1), (2, 1)); // owner item 2
        await response.ExpectValidationAsync("units");
    }

    [Fact]
    public async Task Invalid_units_are_VALIDATION_FAILED()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);

        (int, int)[][] cases = [[], [(1, 0)], [(1, -1)], [(1, 1), (1, 1)], [(9, 1)]];
        foreach (var units in cases)
        {
            using var response = await RequestAsync(t, order.OrderId, customer, units);
            await response.ExpectValidationAsync("units");
        }
        using var badId = await t.SendAsync(HttpMethod.Post, "/api/returns", new { orderId = "nope", units = new[] { new { lineNo = 1, quantity = 1 } } }, customer);
        await badId.ExpectValidationAsync("order_id");
    }

    [Fact]
    public async Task Return_tracking_receive_and_inspect()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await Seed.ReturnAsync(t, order.OrderId, customer, (1, 2));
        await Seed.RunLabelWorkerAsync(t);
        var tracking = (await ReturnShipmentAsync(t, returnId)).TrackingNumber!;

        await Seed.TrackAsync(t, tracking, "TRANSIT");
        Assert.Equal(ShipmentStatuses.InTransit, (await ReturnShipmentAsync(t, returnId)).Status);
        Assert.Equal(ReturnStatuses.InTransit, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        await Seed.TrackAsync(t, tracking, "DELIVERED");
        Assert.Equal(ShipmentStatuses.Delivered, (await ReturnShipmentAsync(t, returnId)).Status);
        Assert.Equal(ReturnStatuses.InTransit, (await Seed.ReturnRequestAsync(t, returnId)).Status); // the warehouse's receive is what counts

        using var received = await ReceiveAsync(t, returnId);
        Assert.Equal("received", (await received.OkJsonAsync()).GetProperty("status").GetString());
        var message = Assert.Single(await Outbox.PayloadsAsync<ReturnReceivedV1>(t, FulfillmentMessages.ReturnReceived));
        Assert.Equal((order.OrderId, returnId), (message.OrderId, message.ReturnId));
        var receiveAudit = (await Outbox.AuditAsync(t)).Last();
        Assert.Equal(("fulfillment.receive-return", "return_request", returnId.ToString(), Warehouse.Id), (receiveAudit.Action, receiveAudit.TargetType, receiveAudit.TargetId, receiveAudit.ActorId));

        using var inspected = await InspectAsync(t, returnId, new[] { Inspect(1, serials[1].ToLowerInvariant(), "damaged", "dented"), Inspect(2, "NOTSHIPPED", "ok") });
        var body = await inspected.OkJsonAsync();

        Assert.Equal("inspected", body.GetProperty("status").GetString());
        Assert.Equal(ReturnStatuses.Inspected, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        var units = await Seed.UnitsAsync(t, returnId);
        Assert.Equal([(serials[1], "damaged", "dented"), ("NOTSHIPPED", "wrong_item", null)], units.Select(u => (u.ReceivedSerial, u.InspectionResult, u.InspectionNotes)).ToArray());
        Assert.Equal(["damaged", "wrong_item"], body.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("inspectionResult").GetString()).ToArray());
        var inspectAudit = (await Outbox.AuditAsync(t)).Last();
        Assert.Equal(("fulfillment.record-inspection", "return_request", returnId.ToString(), Warehouse.Id), (inspectAudit.Action, inspectAudit.TargetType, inspectAudit.TargetId, inspectAudit.ActorId));
    }

    [Fact]
    public async Task Inspection_must_cover_every_unit()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t, b => b.Line(3, quantity: 1)); // line 3: product, not serialized
        var returnId = await Seed.ReceivedReturnAsync(t, order.OrderId, customer, (1, 1), (3, 1));

        object[][] bad =
        [
            [Inspect(1, serials[0])],                                       // unit 2 missing
            [Inspect(1, serials[0]), Inspect(1, serials[1]), Inspect(2)],   // unit 1 twice
            [Inspect(1, serials[0]), Inspect(3)],                           // no unit 3
            [Inspect(1, serials[0]), Inspect(2, result: "broken")],         // not a result
            [Inspect(1, serials[0]), Inspect(2, notes: new string('x', 501))],
            [Inspect(1), Inspect(2)],                                       // serialized without a serial
            [Inspect(1, "no good!"), Inspect(2)],                           // not a serial
            [Inspect(1, serials[0]), Inspect(2, serials[1])],               // non-serialized with a serial
            [],
        ];
        foreach (var units in bad)
        {
            using var response = await InspectAsync(t, returnId, units);
            await response.ExpectValidationAsync("units");
        }
        Assert.Equal(ReturnStatuses.Received, (await Seed.ReturnRequestAsync(t, returnId)).Status);

        using var ok = await InspectAsync(t, returnId, new[] { Inspect(1, serials[0]), Inspect(2, notes: new string('x', 500)) });
        await ok.OkJsonAsync();
        Assert.Equal(ReturnStatuses.Inspected, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        using var twice = await InspectAsync(t, returnId, new[] { Inspect(1, serials[0]), Inspect(2) });
        await twice.ExpectErrorAsync(400, "INVALID_RETURN_STATE");
    }

    [Fact]
    public async Task Duplicate_serial_in_one_inspection_is_VALIDATION_FAILED()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await Seed.ReceivedReturnAsync(t, order.OrderId, customer, (1, 2));

        using var response = await InspectAsync(t, returnId, new[] { Inspect(1, serials[0]), Inspect(2, serials[0].ToLowerInvariant()) });

        await response.ExpectValidationAsync("units");
        Assert.Equal(ReturnStatuses.Received, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        Assert.All(await Seed.UnitsAsync(t, returnId), u => Assert.Equal((null, null), (u.ReceivedSerial, u.InspectionResult)));
    }

    [Fact]
    public async Task Serial_of_another_line_is_wrong_item()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        string[] serials = [$"LA{id}", $"LB{id}", $"LC{id}"]; // lines: 1 has two units, 3 has one
        var ready = Seed.Ready(b => b.Line(3, serialized: true, quantity: 1));
        await Seed.ReadyAsync(t, ready);
        await Seed.DeliveredAsync(t, ready, serials);
        var customer = TestCaller.Customer(ready.AccountId);
        var returnId = await Seed.ReceivedReturnAsync(t, ready.OrderId, customer, (1, 2), (3, 1));

        using var response = await InspectAsync(t, returnId, new[] { Inspect(1, serials[0]), Inspect(2, serials[2]), Inspect(3, serials[1]) }); // unit 2 is line 1, unit 3 is line 3
        await response.OkJsonAsync();

        Assert.Equal([("ok", serials[0]), ("wrong_item", serials[2]), ("wrong_item", serials[1])],
            (await Seed.UnitsAsync(t, returnId)).Select(u => (u.InspectionResult!, u.ReceivedSerial!)).ToArray());
    }

    [Theory]
    [InlineData(ReturnStatuses.Inspected, null, "wrong_item")]
    [InlineData(ReturnStatuses.Decided, Decisions.Full, "wrong_item")]
    [InlineData(ReturnStatuses.Decided, Decisions.Reject, "ok")]
    public async Task Serial_held_by_another_return_of_the_order_is_wrong_item(string firstStatus, string? firstDecision, string expected)
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var first = await Seed.InspectedReturnAsync(t, order.OrderId, customer, (1, 1));
        var held = (await Seed.UnitsAsync(t, first)).Single().ReceivedSerial!;
        Assert.Contains(held, serials);
        await using (var db = t.NewDb())
        {
            await db.ReturnRequests.Where(r => r.Id == first).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, firstStatus), Ct);
            await db.ReturnUnits.Where(u => u.ReturnId == first).ExecuteUpdateAsync(s => s.SetProperty(u => u.Decision, firstDecision), Ct);
        }
        var second = await Seed.ReceivedReturnAsync(t, order.OrderId, customer, (1, 1));

        using var response = await InspectAsync(t, second, new[] { Inspect(1, held) });
        await response.OkJsonAsync();

        Assert.Equal(expected, (await Seed.UnitsAsync(t, second)).Single().InspectionResult);
    }

    [Fact]
    public async Task Receive_in_wrong_state_is_INVALID_RETURN_STATE()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await Seed.ReturnAsync(t, order.OrderId, customer, (1, 1));

        using (var early = await ReceiveAsync(t, returnId)) await early.ExpectErrorAsync(400, "INVALID_RETURN_STATE"); // requested: no label yet
        using (var notReceived = await InspectAsync(t, returnId, new[] { Inspect(1, serials[0]) })) await notReceived.ExpectErrorAsync(400, "INVALID_RETURN_STATE");
        await Seed.RunLabelWorkerAsync(t);
        using (var asCustomer = await ReceiveAsync(t, returnId, customer)) await asCustomer.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        using (var ok = await ReceiveAsync(t, returnId)) await ok.OkJsonAsync();
        using (var again = await ReceiveAsync(t, returnId)) await again.ExpectErrorAsync(400, "INVALID_RETURN_STATE");
        using (var unknown = await ReceiveAsync(t, Guid.NewGuid())) await unknown.ExpectErrorAsync(404, "NOT_FOUND");
        Assert.Single(await Outbox.PayloadsAsync<ReturnReceivedV1>(t, FulfillmentMessages.ReturnReceived));
    }

    [Fact]
    public async Task F15_return_request_expires()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);
        var returnId = await Seed.ReturnAsync(t, order.OrderId, customer, (1, 2));
        await Seed.RunLabelWorkerAsync(t);
        Assert.Equal(ReturnStatuses.LabelIssued, (await Seed.ReturnRequestAsync(t, returnId)).Status);

        t.Clock.Advance(TimeSpan.FromDays(14) - TimeSpan.FromMinutes(1));
        await SweepAsync(t);
        Assert.Equal(ReturnStatuses.LabelIssued, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        t.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        await SweepAsync(t);

        Assert.Equal(ReturnStatuses.Expired, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        Assert.Equal(ShipmentStatuses.VoidPending, (await ReturnShipmentAsync(t, returnId)).Status);
        var closed = Assert.Single(await Outbox.PayloadsAsync<ReturnClosedV1>(t, FulfillmentMessages.ReturnClosed));
        Assert.Equal((order.OrderId, returnId, "expired"), (closed.OrderId, closed.ReturnId, closed.Reason));

        await Seed.RunLabelWorkerAsync(t);
        Assert.Equal(ShipmentStatuses.Voided, (await ReturnShipmentAsync(t, returnId)).Status);
        Assert.Equal([t.Shippo.Transactions.Last().TransactionId], t.Shippo.Refunds);
        await SweepAsync(t);
        Assert.Single(await Outbox.PayloadsAsync<ReturnClosedV1>(t, FulfillmentMessages.ReturnClosed));
    }

    [Fact]
    public async Task Expiry_before_label_voids_nothing_to_buy() // Review Focus 5
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);
        var returnId = await Seed.ReturnAsync(t, order.OrderId, customer, (1, 1));
        var bought = t.Shippo.Transactions.Count; // the outbound label

        t.Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromHours(1));
        await SweepAsync(t);
        await Seed.RunLabelWorkerAsync(t);

        Assert.Equal(ReturnStatuses.Expired, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        Assert.Equal(ShipmentStatuses.Voided, (await ReturnShipmentAsync(t, returnId)).Status);
        Assert.Equal(bought, t.Shippo.Transactions.Count);
        Assert.Empty(t.Shippo.Refunds);
        Assert.Equal("expired", Assert.Single(await Outbox.PayloadsAsync<ReturnClosedV1>(t, FulfillmentMessages.ReturnClosed)).Reason);
    }

    [Fact]
    public async Task Expiry_during_buy_voids_the_bought_label() // Review Focus 5
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);
        var returnId = await Seed.ReturnAsync(t, order.OrderId, customer, (1, 1));
        t.Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromHours(1));
        Task sweep = Task.CompletedTask;
        var sweepStarted = false;
        t.Shippo.BeforeTransaction = () =>
        {
            sweepStarted = true;
            sweep = SweepAsync(t); // waits for the label worker's lock on the shipment
            LabelTests.WaitUntilSomeoneWaitsForALock(t.ConnectionString);
        };

        await Seed.RunLabelWorkerAsync(t);

        Assert.True(sweepStarted);
        await sweep;
        Assert.Equal(ReturnStatuses.Expired, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        var shipment = await ReturnShipmentAsync(t, returnId);
        var transactionId = t.Shippo.Transactions.Last().TransactionId;
        Assert.Equal(2, t.Shippo.Transactions.Count); // the outbound label and this one
        Assert.Equal(transactionId, shipment.ShippoTransactionId);
        Assert.Contains(shipment.Status, new[] { ShipmentStatuses.VoidPending, ShipmentStatuses.Voided });

        t.Shippo.BeforeTransaction = null;
        await Seed.RunLabelWorkerAsync(t);

        Assert.Equal(ShipmentStatuses.Voided, (await ReturnShipmentAsync(t, returnId)).Status);
        Assert.Equal([transactionId], t.Shippo.Refunds);
        Assert.Equal(2, t.Shippo.Transactions.Count);
    }

    [Fact]
    public async Task Expiry_leaves_a_carrier_scanned_return_alone()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);
        var returnId = await Seed.ReturnAsync(t, order.OrderId, customer, (1, 1));
        await Seed.RunLabelWorkerAsync(t);
        await Seed.TrackAsync(t, (await ReturnShipmentAsync(t, returnId)).TrackingNumber!, "DELIVERED"); // no TRANSIT event came

        t.Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromHours(1));
        await SweepAsync(t);

        Assert.Equal(ShipmentStatuses.Delivered, (await ReturnShipmentAsync(t, returnId)).Status);
        Assert.Equal(ReturnStatuses.LabelIssued, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<ReturnClosedV1>(t, FulfillmentMessages.ReturnClosed));
        using var receive = await ReceiveAsync(t, returnId);
        Assert.Equal("received", (await receive.OkJsonAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Expiry_leaves_in_transit_returns_alone()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);
        var returnId = await Seed.ReturnAsync(t, order.OrderId, customer, (1, 1));
        await Seed.RunLabelWorkerAsync(t);
        await Seed.TrackAsync(t, (await ReturnShipmentAsync(t, returnId)).TrackingNumber!, "TRANSIT");

        t.Clock.Advance(TimeSpan.FromDays(30));
        await SweepAsync(t);

        Assert.Equal(ReturnStatuses.InTransit, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        Assert.Equal(ShipmentStatuses.InTransit, (await ReturnShipmentAsync(t, returnId)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<ReturnClosedV1>(t, FulfillmentMessages.ReturnClosed));
    }

    [Fact]
    public async Task Lists_page_and_filter()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (orderA, _, customerA) = await DeliveredAsync(t);
        var (orderB, _, customerB) = await DeliveredAsync(t);
        var r1 = await Seed.ReceivedReturnAsync(t, orderA.OrderId, customerA, (1, 1));
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        var r2 = await Seed.ReturnAsync(t, orderA.OrderId, customerA, (1, 1));
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        var r3 = await Seed.ReturnAsync(t, orderB.OrderId, customerB, (1, 2));

        async Task<string[]> ListAsync(string path, TestCaller caller)
        {
            using var response = await t.SendAsync(HttpMethod.Get, path, caller: caller);
            return (await response.OkJsonAsync()).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();
        }
        string[] Ids(params Guid[] ids) => ids.Select(i => i.ToString()).ToArray();
        var support = TestCaller.Staff("support");

        Assert.Equal(Ids(r1, r2, r3), await ListAsync(Admin, support));
        Assert.Equal(Ids(r1, r2, r3), await ListAsync(Admin, Warehouse));
        Assert.Equal(Ids(r2, r3), await ListAsync($"{Admin}?status=requested", support));
        Assert.Equal(Ids(r1), await ListAsync($"{Admin}?status=received", support));
        Assert.Equal(Ids(r2), await ListAsync($"{Admin}?pageSize=1&page=2", support));
        Assert.Empty(await ListAsync($"{Admin}?status=decided", support));
        using (var bad = await t.SendAsync(HttpMethod.Get, $"{Admin}?status=nope", caller: support)) await bad.ExpectValidationAsync("status");
        using (var refused = await t.SendAsync(HttpMethod.Get, Admin, caller: customerA)) await refused.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");

        Assert.Equal(Ids(r2, r1), await ListAsync("/api/returns", customerA)); // newest first, only A's
        Assert.Equal(Ids(r1), await ListAsync("/api/returns?pageSize=1&page=2", customerA));
        Assert.Equal(Ids(r3), await ListAsync("/api/returns", customerB));
        Assert.Empty(await ListAsync("/api/returns", TestCaller.Customer()));
        using (var staff = await t.SendAsync(HttpMethod.Get, "/api/returns", caller: support)) await staff.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }
}
