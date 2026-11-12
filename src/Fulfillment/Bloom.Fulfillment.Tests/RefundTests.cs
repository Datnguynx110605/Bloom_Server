using System.Text.Json;
using Bloom.Fulfillment.Consumers;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Identity.Contracts;
using Bloom.Order.Contracts;
using Bloom.Payment.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Tests;

public class RefundTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TestCaller Support = TestCaller.Staff("support");
    private const string Admin = "/api/admin/fulfillment/returns";

    /// <summary>A delivered order (device line 1 with two serials, plan line 2 unless removed) and the serials shipped, in scan order. Fresh serials per order: an open coverage keeps its serial.</summary>
    private static async Task<(OrderReadyToFulfillV1 Order, string[] Serials, TestCaller Customer)> DeliveredAsync(TestFulfillment t, Action<Seed.ReadyBuilder>? configure = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var ready = Seed.Ready(configure);
        await Seed.ReadyAsync(t, ready);
        string[] serials = [$"SNB{id}", $"SNA{id}"];
        await Seed.DeliveredAsync(t, ready, serials);
        return (ready, serials, TestCaller.Customer(ready.AccountId));
    }

    private static void NoPlan(Seed.ReadyBuilder b) => b.Lines.RemoveAll(l => l.Kind == "warranty_plan");

    /// <summary>A serialized device line 1 of two units paid <paramref name="paid"/>, no plan.</summary>
    private static Action<Seed.ReadyBuilder> Device(long paid, long tax = 0) =>
        b => { NoPlan(b); b.Line(1, serialized: true, quantity: 2, paid: paid, tax: tax); };

    private static object Full(int unitNo) => new { unitNo, decision = "full", partialCents = 0 };

    private static object Partial(int unitNo, long cents) => new { unitNo, decision = "partial", partialCents = cents };

    private static object Reject(int unitNo) => new { unitNo, decision = "reject", partialCents = 0 };

    private static Task<HttpResponseMessage> DecideAsync(TestFulfillment t, Guid returnId, object[] units, Guid? key = null, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/{returnId}/decision", new { units }, caller ?? Support, key ?? Guid.NewGuid());

    private static async Task<JsonElement> DecidedAsync(TestFulfillment t, Guid returnId, params object[] units)
    {
        using var response = await DecideAsync(t, returnId, units);
        return await response.OkJsonAsync();
    }

    /// <summary>A received return whose units are inspected with the given serials, one per unit in unit order (empty for non-serialized lines); <c>ok</c> unless the serial is foreign.</summary>
    private static async Task<Guid> InspectedAsync(TestFulfillment t, Guid orderId, TestCaller customer, (int Line, int Qty)[] units, params string[] serials)
    {
        var returnId = await Seed.ReceivedReturnAsync(t, orderId, customer, units);
        using var response = await t.SendAsync(HttpMethod.Post, $"{Admin}/{returnId}/inspection",
            new { units = serials.Select((s, i) => new { unitNo = i + 1, receivedSerial = s, result = "ok" }) }, TestCaller.Staff("warehouse"));
        await response.OkJsonAsync();
        return returnId;
    }

    private static async Task<OrderLineAccount> LineAsync(TestFulfillment t, Guid orderId, int lineNo)
    {
        await using var db = t.NewDb();
        return await db.OrderLineAccounts.AsNoTracking().SingleAsync(l => l.OrderId == orderId && l.LineNo == lineNo, Ct);
    }

    private static async Task<List<FulfillmentSaga>> SagasAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.FulfillmentSagas.AsNoTracking().Where(s => db.ReturnRequests.Any(r => r.Id == s.RefId && r.OrderId == orderId)).ToListAsync(Ct);
    }

    private static async Task<WarrantyCoverage> CoverageAsync(TestFulfillment t, string serial)
    {
        await using var db = t.NewDb();
        return await db.WarrantyCoverages.AsNoTracking().SingleAsync(c => c.Serial == serial, Ct);
    }

    private static async Task<Shipment> ShipBackAsync(TestFulfillment t, Guid returnId)
    {
        await using var db = t.NewDb();
        return await db.Shipments.AsNoTracking().SingleAsync(s => s.ReturnRequestId == returnId && s.Direction == Directions.ShipBack, Ct);
    }

    private static long Cents(JsonElement e, string name = "amountCents") => long.Parse(e.GetProperty(name).GetString()!);

    private static (int Line, long Cents)[] Lines(JsonElement body) =>
        body.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("lineNo").GetInt32(), Cents(l))).ToArray();

    [Fact]
    public void Unit_share_math()
    {
        Assert.Equal((500L, 0L), RefundMath.UnitShare(1001, 0, 2, 0));
        Assert.Equal((501L, 0L), RefundMath.UnitShare(1001, 0, 2, 1)); // owner item 1: units, not cents, decide the last share, so a partial on unit 1 changes nothing
        Assert.Equal((1000L, 80L), RefundMath.UnitShare(2000, 160, 2, 0));
        Assert.Equal((335L, 28L), RefundMath.UnitShare(1001, 80, 3, 2)); // 1001 - 2 * 333, 80 - 2 * 26
        Assert.Equal((1001L, 80L), RefundMath.UnitShare(1001, 80, 1, 0));

        Assert.Equal(24, RefundMath.PartialTax(300, 1000, 80)); // 24.0
        Assert.Equal(1, RefundMath.PartialTax(1, 2, 1)); // 0.5 rounds up (RET-R14)
        Assert.Equal(80, RefundMath.PartialTax(1000, 1000, 80));
    }

    [Fact]
    public async Task F09_two_units_of_a_1001_cent_line_returned_separately()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t, Device(1001));
        var first = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], serials[0]);
        var second = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], serials[1]);

        var a = await DecidedAsync(t, first, Full(1));
        var b = await DecidedAsync(t, second, Full(1));

        Assert.Equal((500L, 501L), (Cents(a), Cents(b)));
        Assert.Equal([(1, 500L)], Lines(a));
        Assert.Equal([(1, 501L)], Lines(b));
        var line = await LineAsync(t, order.OrderId, 1);
        Assert.Equal((2, 1001L), (line.RefundedUnits, line.RefundedCents));
        var commands = await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment);
        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.Equal(("return", order.OrderId), (c.Reason, c.OrderId)));
        Assert.Equal([500L, 501L], commands.Select(c => c.AmountCents));
        Assert.Equal([(1, 500L)], commands[0].Lines!.Select(l => (l.LineNo, l.AmountCents)));
        Assert.Equal([(1, 501L)], commands[1].Lines!.Select(l => (l.LineNo, l.AmountCents)));
        Assert.Equal(commands.Select(c => c.RefundId.ToString()), [a.GetProperty("refundId").GetString(), b.GetProperty("refundId").GetString()]);
    }

    [Fact]
    public async Task F10_partial_300_on_a_1000_share_with_80_tax()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t, Device(2000, 160));
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], serials[0]);

        var body = await DecidedAsync(t, returnId, Partial(1, 300));

        var unit = Assert.Single(await Seed.UnitsAsync(t, returnId));
        Assert.Equal((Decisions.Partial, 300L, 24L), (unit.Decision, unit.RefundCents, unit.RefundTaxCents));
        Assert.Equal(300, Cents(body));
        var line = await LineAsync(t, order.OrderId, 1);
        Assert.Equal((1, 300L), (line.RefundedUnits, line.RefundedCents));
        Assert.Equal(ReturnStatuses.Decided, (await Seed.ReturnRequestAsync(t, returnId)).Status);
    }

    [Fact]
    public async Task Partial_above_share_is_PARTIAL_EXCEEDS_SHARE()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t, Device(2000, 160));
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], serials[0]);
        var key = Guid.NewGuid();

        using (var over = await DecideAsync(t, returnId, [Partial(1, 1001)], key)) await over.ExpectErrorAsync(400, "PARTIAL_EXCEEDS_SHARE");
        using (var replay = await DecideAsync(t, returnId, [Partial(1, 1001)], key)) await replay.ExpectErrorAsync(400, "PARTIAL_EXCEEDS_SHARE"); // the key stores the rejection

        Assert.Equal(ReturnStatuses.Inspected, (await Seed.ReturnRequestAsync(t, returnId)).Status);
        Assert.Null(Assert.Single(await Seed.UnitsAsync(t, returnId)).Decision);
        Assert.Equal((0, 0L), ((await LineAsync(t, order.OrderId, 1)).RefundedUnits, (await LineAsync(t, order.OrderId, 1)).RefundedCents));
        Assert.Empty(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Empty(await SagasAsync(t, order.OrderId));
        await DecidedAsync(t, returnId, Partial(1, 1000)); // exactly the share is fine, with a fresh key
    }

    [Fact]
    public async Task F11_full_return_of_all_units_adds_the_express_fee_once()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t, b => b.Express(1500)); // device 2 x 1001, plan 9900 on serials[0]
        // serials[1] has no plan. The second unit comes back as a foreign serial: refunded, but the plan stays (WAR-R05 only for the shipped serial).
        var first = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], serials[1]);
        var second = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], "FOREIGN" + serials[0]);

        var a = await DecidedAsync(t, first, Full(1));
        Assert.Equal(1001, Cents(a));
        Assert.False((await ShippingRefundedAsync(t, order.OrderId)));
        var b = await DecidedAsync(t, second, Full(1));
        Assert.Equal(1001 + 1500, Cents(b)); // the Express fee joins the last unit
        Assert.Equal([(1, 1001L)], Lines(b)); // shipping is in the amount, not in the lines
        Assert.True(await ShippingRefundedAsync(t, order.OrderId));

        using var planOnly = await t.SendAsync(HttpMethod.Post, "/api/returns", new { orderId = order.OrderId, units = new[] { new { lineNo = 2, quantity = 1 } } }, customer, Guid.NewGuid());
        await planOnly.OkJsonAsync();
        var commands = await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment);
        Assert.Equal([1001L, 2501L, 9900L], commands.Select(c => c.AmountCents)); // no second fee
    }

    private static async Task<bool> ShippingRefundedAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.OrderAccounts.AsNoTracking().Where(a => a.OrderId == orderId).Select(a => a.ShippingRefunded).SingleAsync(Ct);
    }

    [Fact]
    public async Task Partial_device_refund_refunds_its_plan_in_full()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await Seed.InspectedReturnAsync(t, order.OrderId, customer, (1, 1)); // takes serials[0] (scan order), which carries the plan

        var body = await DecidedAsync(t, returnId, Partial(1, 300));

        Assert.Equal(300 + 9900, Cents(body));
        Assert.Equal([(1, 300L), (2, 9900L)], Lines(body));
        Assert.Equal(CoverageStatuses.Cancelled, (await CoverageAsync(t, serials[0])).Status);
        var plan = await LineAsync(t, order.OrderId, 2);
        Assert.Equal((1, 9900L), (plan.RefundedUnits, plan.RefundedCents));
        var units = await Seed.UnitsAsync(t, returnId);
        Assert.Equal([1, 2], units.Select(u => u.UnitNo));
        Assert.Equal((2, Decisions.Full, 9900L, 800L, (string?)null), (units[1].LineNo, units[1].Decision, units[1].RefundCents, units[1].RefundTaxCents, units[1].ReceivedSerial));
        var decided = Assert.Single(await Outbox.PayloadsAsync<ReturnDecidedV1>(t, FulfillmentMessages.ReturnDecided));
        Assert.Equal([(1, 1), (2, 1)], decided.Accepted.Select(a => (a.LineNo, a.Quantity)));
        Assert.Equal(10200, decided.RefundCents);
    }

    [Fact]
    public async Task Wrong_item_rejected_is_shipped_back()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], "FOREIGN" + serials[0]);
        Assert.Equal(InspectionResults.WrongItem, Assert.Single(await Seed.UnitsAsync(t, returnId)).InspectionResult);

        var body = await DecidedAsync(t, returnId, Reject(1));

        Assert.Equal(("", 0L), (body.GetProperty("refundId").GetString(), Cents(body)));
        Assert.Empty(body.GetProperty("lines").EnumerateArray());
        Assert.Empty(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment)); // owner item 4
        Assert.Empty(await SagasAsync(t, order.OrderId));
        var shipBack = await ShipBackAsync(t, returnId);
        Assert.Equal((ShipmentStatuses.LabelPending, ShippingOptions.Standard, order.OrderId, (Guid?)null), (shipBack.Status, shipBack.Service, shipBack.OrderId, shipBack.PackTaskId));
        var decided = Assert.Single(await Outbox.PayloadsAsync<ReturnDecidedV1>(t, FulfillmentMessages.ReturnDecided));
        Assert.Equal((0L, 0), (decided.RefundCents, decided.Accepted.Count));
        Assert.Empty(await Outbox.PayloadsAsync<ReceiveCustomerReturnV1>(t, FulfillmentMessages.ReceiveCustomerReturn));
        Assert.NotEqual(CoverageStatuses.Cancelled, (await CoverageAsync(t, serials[0])).Status);
        Assert.Equal(Decisions.Reject, Assert.Single(await Seed.UnitsAsync(t, returnId)).Decision);
    }

    [Fact]
    public async Task Non_serialized_units_reach_the_inventory_command_with_a_null_serial()   // the outbox writes with BloomJson, which drops nulls unless the property says otherwise
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t, b => { NoPlan(b); b.Line(1, quantity: 2, paid: 2000, tax: 0); });
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 2)], "", "");

        await DecidedAsync(t, returnId, Full(1), Full(2));

        var command = Assert.Single(await Outbox.PayloadsAsync<ReceiveCustomerReturnV1>(t, FulfillmentMessages.ReceiveCustomerReturn));
        Assert.Equal((order.Lines[0].SkuId, (string?)null, 2), Assert.Single(command.Units) is var u ? (u.SkuId, u.Serial, u.Quantity) : default);
        await using var db = t.NewDb();
        var payload = await db.Database.SqlQuery<string>($"SELECT payload AS \"Value\" FROM outbox_messages WHERE message_type = {FulfillmentMessages.ReceiveCustomerReturn}").SingleAsync(Ct);
        Assert.Contains("\"serial\": null", payload); // jsonb prints ": "
    }

    [Fact]
    public async Task Accepted_wrong_item_is_left_out_of_the_inventory_command()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 2)], serials[1], "FOREIGN" + serials[0]); // unit 2 is wrong_item

        var body = await DecidedAsync(t, returnId, Full(1), Full(2));

        Assert.Equal(2002, Cents(body)); // both units are refunded for their own share; no plan: the covered serial never came back
        var command = Assert.Single(await Outbox.PayloadsAsync<ReceiveCustomerReturnV1>(t, FulfillmentMessages.ReceiveCustomerReturn));
        var unit = Assert.Single(command.Units);
        Assert.Equal((order.Lines[0].SkuId, serials[1], 1), (unit.SkuId, unit.Serial, unit.Quantity));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Warning]") && e.Contains("FOREIGN" + serials[0]));
        Assert.NotEqual(CoverageStatuses.Cancelled, (await CoverageAsync(t, serials[0])).Status);
        Assert.Equal(0, (await LineAsync(t, order.OrderId, 2)).RefundedUnits);
        var decided = Assert.Single(await Outbox.PayloadsAsync<ReturnDecidedV1>(t, FulfillmentMessages.ReturnDecided));
        Assert.Equal([(1, 2)], decided.Accepted.Select(a => (a.LineNo, a.Quantity)));
    }

    [Fact]
    public async Task F14_decide_retried_with_the_same_key_sends_one_refund()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t, Device(2002));
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], serials[0]);
        var key = Guid.NewGuid();

        using var first = await DecideAsync(t, returnId, [Full(1)], key);
        var a = await first.OkJsonAsync();
        using var second = await DecideAsync(t, returnId, [Full(1)], key);
        var b = await second.OkJsonAsync();

        Assert.Equal(a.ToString(), b.ToString());
        Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Single(await SagasAsync(t, order.OrderId));
        Assert.Single(await Outbox.PayloadsAsync<ReturnDecidedV1>(t, FulfillmentMessages.ReturnDecided));
        Assert.Equal(1, (await LineAsync(t, order.OrderId, 1)).RefundedUnits);
        using var other = await DecideAsync(t, returnId, [Partial(1, 5)], key);
        await other.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task Decide_writes_command_saga_and_lines_in_one_transaction()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 2)], serials[0], serials[1]);
        var staff = TestCaller.Staff("admin");

        using var response = await DecideAsync(t, returnId, [Full(1), Full(2)], caller: staff);
        var body = await response.OkJsonAsync();

        var refundId = Guid.Parse(body.GetProperty("refundId").GetString()!);
        Assert.Equal(1001 + 1001 + 9900, Cents(body));
        var saga = Assert.Single(await SagasAsync(t, order.OrderId));
        Assert.Equal((returnId, SagaTypes.ReturnRefund, refundId, (DateTimeOffset?)null), (saga.RefId, saga.Type, saga.RefundId, saga.CompletedAt));
        Assert.Equal(["refund"], saga.PendingSteps);
        var command = Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Equal((order.OrderId, refundId, 11902L, "return"), (command.OrderId, command.RefundId, command.AmountCents, command.Reason));
        Assert.Equal([(1, 2002L), (2, 9900L)], command.Lines!.Select(l => (l.LineNo, l.AmountCents)));
        var line = await LineAsync(t, order.OrderId, 1);
        Assert.Equal((2, 2002L), (line.RefundedUnits, line.RefundedCents));
        var inventory = Assert.Single(await Outbox.PayloadsAsync<ReceiveCustomerReturnV1>(t, FulfillmentMessages.ReceiveCustomerReturn));
        Assert.Equal(order.OrderId, inventory.OrderId);
        Assert.Equal([(order.Lines[0].SkuId, serials[0], 1), (order.Lines[0].SkuId, serials[1], 1)], inventory.Units.Select(u => (u.SkuId, u.Serial, u.Quantity)));
        var closed = Assert.Single(await Outbox.PayloadsAsync<ReturnClosedV1>(t, FulfillmentMessages.ReturnClosed));
        Assert.Equal((order.OrderId, returnId, "decided"), (closed.OrderId, closed.ReturnId, closed.Reason));
        var audit = Assert.Single(await Outbox.AuditAsync(t), x => x.Action == "fulfillment.decide-return");
        Assert.Equal(("fulfillment", "fulfillment.decide-return", "return_request", returnId.ToString(), staff.Id, "admin"),
            (audit.Service, audit.Action, audit.TargetType, audit.TargetId, audit.ActorId, audit.ActorRole));
        Assert.Equal(11902, metrics.Sum("refund_amount_cents_total", ("reason", "return")));
        await using var db = t.NewDb();
        Assert.False(await db.Shipments.AnyAsync(s => s.ReturnRequestId == returnId && s.Direction == Directions.ShipBack, Ct));
    }

    [Fact]
    public async Task Concurrent_decisions_on_one_line_refund_500_and_501()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        for (var round = 0; round < 20; round++)
        {
            var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var ready = Seed.Ready(b => { NoPlan(b); b.Line(1, serialized: true, quantity: 2, paid: 1001, tax: 0); });
            await Seed.ReadyAsync(t, ready);
            await Seed.DeliveredAsync(t, ready, $"SNB{id}", $"SNA{id}");
            var customer = TestCaller.Customer(ready.AccountId);
            var first = await Seed.InspectedReturnAsync(t, ready.OrderId, customer, (1, 1));
            var second = await Seed.InspectedReturnAsync(t, ready.OrderId, customer, (1, 1));

            var bodies = await Task.WhenAll(DecidedAsync(t, first, Full(1)), DecidedAsync(t, second, Full(1)));

            Assert.Equal([500L, 501L], bodies.Select(b => Cents(b)).Order()); // never 500 twice
            var line = await LineAsync(t, ready.OrderId, 1);
            Assert.Equal((2, 1001L), (line.RefundedUnits, line.RefundedCents));
        }
    }

    [Fact]
    public async Task Plan_only_request_is_decided_at_once()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t, b => b.Line(2, "warranty_plan", quantity: 2, parent: 1, months: 12, paid: 19800, tax: 1600));
        Assert.Equal(CoverageStatuses.Active, (await CoverageAsync(t, serials[0])).Status);

        async Task<JsonElement> RequestPlansAsync(int quantity)
        {
            using var response = await t.SendAsync(HttpMethod.Post, "/api/returns", new { orderId = order.OrderId, units = new[] { new { lineNo = 2, quantity } } }, customer, Guid.NewGuid());
            return await response.OkJsonAsync();
        }

        var body = await RequestPlansAsync(1);

        var returnId = Guid.Parse(body.GetProperty("id").GetString()!);
        Assert.Equal("decided", body.GetProperty("status").GetString());
        var unit = Assert.Single(body.GetProperty("units").EnumerateArray());
        Assert.Equal((1, 2, "full", 9900L, 800L), (unit.GetProperty("unitNo").GetInt32(), unit.GetProperty("lineNo").GetInt32(), unit.GetProperty("decision").GetString(),
            Cents(unit, "refundCents"), Cents(unit, "refundTaxCents")));
        Assert.Equal(CoverageStatuses.Cancelled, (await CoverageAsync(t, serials[1])).Status); // the LAST serial by seq
        Assert.Equal(CoverageStatuses.Active, (await CoverageAsync(t, serials[0])).Status);
        var plan = await LineAsync(t, order.OrderId, 2);
        Assert.Equal((1, 9900L), (plan.RefundedUnits, plan.RefundedCents));

        var command = Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Equal((order.OrderId, 9900L, "return"), (command.OrderId, command.AmountCents, command.Reason));
        Assert.Equal([(2, 9900L)], command.Lines!.Select(l => (l.LineNo, l.AmountCents)));
        var saga = Assert.Single(await SagasAsync(t, order.OrderId));
        Assert.Equal((returnId, SagaTypes.ReturnRefund, command.RefundId), (saga.RefId, saga.Type, saga.RefundId));
        Assert.Equal(["refund"], saga.PendingSteps);
        var requested = Assert.Single(await Outbox.PayloadsAsync<ReturnRequestedV1>(t, FulfillmentMessages.ReturnRequested));
        Assert.Equal((order.OrderId, returnId), (requested.OrderId, requested.ReturnId));
        var decided = Assert.Single(await Outbox.PayloadsAsync<ReturnDecidedV1>(t, FulfillmentMessages.ReturnDecided));
        Assert.Equal([(2, 1)], decided.Accepted.Select(a => (a.LineNo, a.Quantity)));
        Assert.Equal(9900, decided.RefundCents);
        Assert.Equal("decided", Assert.Single(await Outbox.PayloadsAsync<ReturnClosedV1>(t, FulfillmentMessages.ReturnClosed)).Reason);
        await using (var db = t.NewDb()) Assert.False(await db.Shipments.AnyAsync(s => s.ReturnRequestId == returnId, Ct)); // no label, no inspection

        var again = await RequestPlansAsync(1); // the last unit takes the rest of the line
        Assert.Equal(9900, Cents(again.GetProperty("units")[0], "refundCents"));
        Assert.Equal(CoverageStatuses.Cancelled, (await CoverageAsync(t, serials[0])).Status);
        using var more = await t.SendAsync(HttpMethod.Post, "/api/returns", new { orderId = order.OrderId, units = new[] { new { lineNo = 2, quantity = 1 } } }, customer, Guid.NewGuid());
        await more.ExpectErrorAsync(400, "RETURN_QUANTITY_EXCEEDED");
    }

    [Fact]
    public async Task Plan_alone_after_window_is_RETURN_WINDOW_CLOSED()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, _, customer) = await DeliveredAsync(t);
        t.Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromSeconds(1));

        using var response = await t.SendAsync(HttpMethod.Post, "/api/returns", new { orderId = order.OrderId, units = new[] { new { lineNo = 2, quantity = 1 } } }, customer, Guid.NewGuid());

        await response.ExpectErrorAsync(400, "RETURN_WINDOW_CLOSED");
        Assert.Empty(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
    }

    [Fact]
    public async Task Refund_completed_closes_the_saga_and_failed_keeps_it_open()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t, Device(2002));
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], serials[0]);
        var refundId = Guid.Parse((await DecidedAsync(t, returnId, Full(1))).GetProperty("refundId").GetString()!);

        await Consume.Async<RefundFailedConsumer>(t, PaymentMessages.RefundFailed, new RefundFailedV1(order.OrderId, refundId, "card_declined"));
        var open = Assert.Single(await SagasAsync(t, order.OrderId));
        Assert.Equal(["refund"], open.PendingSteps);
        Assert.Null(open.CompletedAt);
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains("card_declined"));

        var unknown = Guid.NewGuid();
        await Consume.Async<RefundFailedConsumer>(t, PaymentMessages.RefundFailed, new RefundFailedV1(order.OrderId, unknown, "x"));
        await Consume.Async<RefundCompletedConsumer>(t, PaymentMessages.RefundCompleted, new RefundCompletedV1(order.OrderId, unknown, 5, null));
        Assert.Null(Assert.Single(await SagasAsync(t, order.OrderId)).CompletedAt);

        t.Clock.Advance(TimeSpan.FromMinutes(3));
        await Consume.Async<RefundCompletedConsumer>(t, PaymentMessages.RefundCompleted, new RefundCompletedV1(order.OrderId, refundId, 1001, null));
        var done = Assert.Single(await SagasAsync(t, order.OrderId));
        Assert.Empty(done.PendingSteps);
        Assert.Equal(t.Clock.GetUtcNow(), done.CompletedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Ship_back_handover()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 1)], serials[1]);
        await DecidedAsync(t, returnId, Reject(1));
        var shippedBefore = (await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped)).Count;

        await Seed.RunLabelWorkerAsync(t);
        var shipBack = await ShipBackAsync(t, returnId);
        Assert.Equal(ShipmentStatuses.LabelReady, shipBack.Status);
        Assert.Equal("usps_ground_advantage", t.Shippo.BoughtTokens.Last());
        using var handover = await t.SendAsync(HttpMethod.Post, "/api/admin/fulfillment/handover", new { shipmentId = shipBack.Id }, TestCaller.Staff("warehouse"));
        await handover.OkJsonAsync();

        Assert.Equal(ShipmentStatuses.HandedOver, (await ShipBackAsync(t, returnId)).Status);
        Assert.Equal(shippedBefore, (await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped)).Count);
    }

    [Fact]
    public async Task Decide_validates_authorizes_and_maps_states()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, serials, customer) = await DeliveredAsync(t);
        var returnId = await InspectedAsync(t, order.OrderId, customer, [(1, 2)], serials[0], serials[1]);

        object[][] bad =
        [
            [], [Full(1)], [Full(1), Full(1), Full(2)], [Full(1), Full(3)],
            [Full(1), new { unitNo = 2, decision = "maybe", partialCents = 0 }],
            [Full(1), Partial(2, 0)], [Full(1), Partial(2, -5)], [Full(1), new { unitNo = 2, decision = "full", partialCents = 5 }], [Full(1), new { unitNo = 2, decision = "reject", partialCents = 5 }],
        ];
        foreach (var units in bad)
        {
            using var response = await DecideAsync(t, returnId, units);
            await response.ExpectValidationAsync("units");
        }
        using (var badId = await t.SendAsync(HttpMethod.Post, $"{Admin}/nope/decision", new { units = new[] { Full(1) } }, Support, Guid.NewGuid())) await badId.ExpectValidationAsync("return_id");
        foreach (var caller in new[] { TestCaller.Staff("warehouse"), customer })
            using (var denied = await DecideAsync(t, returnId, [Full(1), Full(2)], caller: caller)) await denied.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        using (var unknown = await DecideAsync(t, Guid.NewGuid(), [Full(1)])) await unknown.ExpectErrorAsync(404, "NOT_FOUND");
        using (var noKey = await t.SendAsync(HttpMethod.Post, $"{Admin}/{returnId}/decision", new { units = new[] { Full(1), Full(2) } }, Support)) await noKey.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REQUIRED");
        Assert.Equal(ReturnStatuses.Inspected, (await Seed.ReturnRequestAsync(t, returnId)).Status);

        var (other, _, otherCustomer) = await DeliveredAsync(t);
        var requested = await Seed.ReturnAsync(t, other.OrderId, otherCustomer, (1, 1));
        using (var early = await DecideAsync(t, requested, [Full(1)])) await early.ExpectErrorAsync(400, "INVALID_RETURN_STATE");
        await DecidedAsync(t, returnId, Full(1), Full(2));
        using (var again = await DecideAsync(t, returnId, [Full(1), Full(2)])) await again.ExpectErrorAsync(400, "INVALID_RETURN_STATE");
    }
}
