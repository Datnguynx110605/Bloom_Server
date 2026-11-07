using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Inventory.Contracts;
using Bloom.Order.Contracts;
using Bloom.Payment.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Fulfillment.Tests;

public class DeliveryFailureTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TestCaller Warehouse = TestCaller.Staff("warehouse");
    private static readonly TestCaller Support = TestCaller.Staff("support");
    private const string Admin = "/api/admin/fulfillment";

    private sealed record Flow(OrderReadyToFulfillV1 Order, Guid TaskId, Guid ShipmentId, string Tracking, string[] Serials);

    /// <summary>A packed order (device line 1 with two serials, its plan line 2, express shipping 1500) handed over to the carrier. Fresh serials per order: an open coverage keeps its serial.</summary>
    private static async Task<Flow> HandedOverAsync(TestFulfillment t, Action<Seed.ReadyBuilder>? configure = null)
    {
        var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var ready = Seed.Ready(b => { b.Express(1500); configure?.Invoke(b); });
        await Seed.ReadyAsync(t, ready);
        string[] serials = [$"SNB{id}", $"SNA{id}"];
        var (taskId, shipmentId) = await Seed.LabelReadyAsync(t, ready, serials);
        using (var response = await t.SendAsync(HttpMethod.Post, $"{Admin}/handover", new { shipmentId }, Warehouse)) await response.OkJsonAsync();
        return new Flow(ready, taskId, shipmentId, (await ShipmentAsync(t, shipmentId)).TrackingNumber!, serials);
    }

    /// <summary>Handed over, then the carrier reports <c>FAILURE</c>: the shipment is <c>possibly_lost</c>.</summary>
    private static async Task<Flow> PossiblyLostAsync(TestFulfillment t, Action<Seed.ReadyBuilder>? configure = null, bool inTransit = false)
    {
        var f = await HandedOverAsync(t, configure);
        if (inTransit) await Seed.TrackAsync(t, f.Tracking, "TRANSIT");
        await Seed.TrackAsync(t, f.Tracking, "FAILURE");
        return f;
    }

    /// <summary>Handed over, then the carrier reports <c>RETURNED</c>: the failure is <c>confirmed</c>.</summary>
    private static async Task<Flow> ReturnedAsync(TestFulfillment t, Action<Seed.ReadyBuilder>? configure = null)
    {
        var f = await HandedOverAsync(t, configure);
        await Seed.TrackAsync(t, f.Tracking, "RETURNED");
        return f;
    }

    private static async Task<Shipment> ShipmentAsync(TestFulfillment t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
    }

    private static async Task<DeliveryFailure> FailureAsync(TestFulfillment t, Guid shipmentId)
    {
        await using var db = t.NewDb();
        return await db.DeliveryFailures.AsNoTracking().SingleAsync(f => f.ShipmentId == shipmentId, Ct);
    }

    private static async Task<List<WarrantyCoverage>> CoveragesAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.WarrantyCoverages.AsNoTracking().Where(c => c.OrderId == orderId).OrderBy(c => c.PlanLineNo).ToListAsync(Ct);
    }

    private static async Task<List<FulfillmentSaga>> SagasAsync(TestFulfillment t, Guid shipmentId)
    {
        await using var db = t.NewDb();
        return await db.FulfillmentSagas.AsNoTracking().Where(s => s.RefId == shipmentId).ToListAsync(Ct);
    }

    private static async Task<List<PackTask>> ReshipTasksAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.PackTasks.AsNoTracking().Where(p => p.OrderId == orderId && p.Kind == TaskKinds.Reship).ToListAsync(Ct);
    }

    private static async Task<List<OrderLineAccount>> LinesAsync(TestFulfillment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.OrderLineAccounts.AsNoTracking().Where(l => l.OrderId == orderId).OrderBy(l => l.LineNo).ToListAsync(Ct);
    }

    private static Task<HttpResponseMessage> RtsAsync(TestFulfillment t, Guid shipmentId, object[] units, Guid? key = null, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/{shipmentId}/returned-to-sender", new { units }, caller ?? Warehouse, key ?? Guid.NewGuid());

    private static Task<HttpResponseMessage> LostAsync(TestFulfillment t, Guid shipmentId, string resolution, Guid? key = null, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/{shipmentId}/lost", new { resolution }, caller ?? Support, key ?? Guid.NewGuid());

    private static object Unit(string serial, string outcome) => new { serial, outcome };

    private static async Task<JsonElement> LostOkAsync(TestFulfillment t, Guid shipmentId, string resolution, Guid? key = null)
    {
        using var response = await LostAsync(t, shipmentId, resolution, key);
        return await response.OkJsonAsync();
    }

    /// <summary>The line amounts of a refund command, by line.</summary>
    private static (int Line, long Cents)[] CommandLines(RefundPaymentV1 c) => c.Lines!.Select(l => (l.LineNo, l.AmountCents)).ToArray();

    [Fact]
    public async Task F12_returned_to_sender_one_sealed_one_failed()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await ReturnedAsync(t);

        using var response = await RtsAsync(t, f.ShipmentId, [Unit(f.Serials[1], "failed"), Unit(f.Serials[0], "sealed")]);
        var body = await response.OkJsonAsync();

        Assert.Equal(("returned_to_sender", "resolved_refund", f.Order.OrderId.ToString()), (body.GetProperty("kind").GetString(), body.GetProperty("status").GetString(), body.GetProperty("orderId").GetString()));
        var received = Assert.Single(await Outbox.PayloadsAsync<ReceiveReturnedToSenderV1>(t, FulfillmentMessages.ReceiveReturnedToSender));
        var deviceSku = f.Order.Lines.Single(l => l.LineNo == 1).SkuId;
        Assert.Equal(f.Order.OrderId, received.RefId);
        Assert.Equal( // "SNA…" sorts before "SNB…"
            [(deviceSku, f.Serials[1], 1, "failed"), (deviceSku, f.Serials[0], 1, "sealed")],
            received.Units.OrderBy(u => u.Serial, StringComparer.Ordinal).Select(u => (u.SkuId, u.Serial!, u.Quantity, u.Outcome)));

        var refund = Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Equal((f.Order.OrderId, "delivery_failure", f.Order.TotalCents), (refund.OrderId, refund.Reason, refund.AmountCents)); // lines + express shipping
        Assert.Equal([(1, 2002L), (2, 9900L)], CommandLines(refund));
        Assert.All(await LinesAsync(t, f.Order.OrderId), l => Assert.Equal((l.Quantity, l.PaidCents), (l.RefundedUnits, l.RefundedCents)));
        await using (var db = t.NewDb()) Assert.True((await db.OrderAccounts.AsNoTracking().SingleAsync(Ct)).ShippingRefunded);

        Assert.All(await CoveragesAsync(t, f.Order.OrderId), c => Assert.Equal(CoverageStatuses.Cancelled, c.Status));
        Assert.Equal(new DeliveryFailedV1(f.Order.OrderId, "returned_to_sender"), Assert.Single(await Outbox.PayloadsAsync<DeliveryFailedV1>(t, FulfillmentMessages.DeliveryFailed)));
        var failure = await FailureAsync(t, f.ShipmentId);
        Assert.Equal((FailureStatuses.ResolvedRefund, Warehouse.Id), (failure.Status, failure.DecidedBy));
        Assert.Contains("sealed", failure.RtsOutcomes);
        Assert.Contains("failed", failure.RtsOutcomes);
        var saga = Assert.Single(await SagasAsync(t, f.ShipmentId));
        Assert.Equal((SagaTypes.DeliveryFailure, refund.RefundId), (saga.Type, saga.RefundId));
        Assert.Equal(["refund"], saga.PendingSteps);
        var audit = Assert.Single(await Outbox.AuditAsync(t), a => a.TargetId == f.ShipmentId.ToString() && a.Action.StartsWith("fulfillment.record"));
        Assert.Equal(("fulfillment.record-returned-to-sender", f.ShipmentId.ToString()), (audit.Action, audit.TargetId));
    }

    [Fact]
    public async Task Returned_to_sender_after_a_partial_earlier_refund_refunds_the_remainder()   // §8
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await ReturnedAsync(t);
        await using (var db = t.NewDb())
            await db.OrderLineAccounts.Where(l => l.OrderId == f.Order.OrderId && l.LineNo == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.RefundedUnits, 1).SetProperty(l => l.RefundedCents, 500L), Ct);

        using var response = await RtsAsync(t, f.ShipmentId, [Unit(f.Serials[0], "sealed"), Unit(f.Serials[1], "sealed")]);
        await response.OkJsonAsync();

        var refund = Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Equal(f.Order.TotalCents - 500, refund.AmountCents);
        Assert.Equal([(1, 1502L), (2, 9900L)], CommandLines(refund));
        Assert.All(await LinesAsync(t, f.Order.OrderId), l => Assert.Equal((l.Quantity, l.PaidCents), (l.RefundedUnits, l.RefundedCents)));
    }

    [Fact]
    public async Task Order_already_fully_refunded_sends_no_refund_command()   // owner item 4
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await ReturnedAsync(t);
        await using (var db = t.NewDb())
        {
            await db.OrderLineAccounts.Where(l => l.OrderId == f.Order.OrderId).ExecuteUpdateAsync(s => s.SetProperty(l => l.RefundedUnits, l => l.Quantity).SetProperty(l => l.RefundedCents, l => l.PaidCents), Ct);
            await db.OrderAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.ShippingRefunded, true), Ct);
        }

        using var response = await RtsAsync(t, f.ShipmentId, [Unit(f.Serials[0], "sealed"), Unit(f.Serials[1], "sealed")]);
        await response.OkJsonAsync();

        Assert.Empty(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Empty(await SagasAsync(t, f.ShipmentId));
        Assert.Single(await Outbox.PayloadsAsync<DeliveryFailedV1>(t, FulfillmentMessages.DeliveryFailed));
        Assert.Equal(FailureStatuses.ResolvedRefund, (await FailureAsync(t, f.ShipmentId)).Status);
    }

    [Fact]
    public async Task Returned_to_sender_units_must_match()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await ReturnedAsync(t, b => b.Line(3, quantity: 2, paid: 500, tax: 0));
        var loose = f.Order.Lines.Single(l => l.LineNo == 3).SkuId;
        object Loose(string outcome) => new { skuId = loose, outcome };
        var (a, b) = (f.Serials[0], f.Serials[1]);

        foreach (var units in new[]
        {
            new[] { Unit(a, "sealed"), Loose("sealed"), Loose("sealed") },                                   // a serial missing
            new[] { Unit(a, "sealed"), Unit(b, "sealed"), Unit("OTHER1", "sealed"), Loose("sealed"), Loose("sealed") }, // an extra serial
            new[] { Unit(a, "sealed"), Unit(a, "sealed"), Loose("sealed"), Loose("sealed") },                 // one serial twice
            new[] { Unit(a, "sealed"), Unit(b, "sealed"), Loose("sealed") },                                  // too few loose units
            new[] { Unit(a, "sealed"), Unit(b, "sealed"), Loose("sealed"), Loose("sealed"), Loose("failed") },  // too many
            new[] { Unit(a, "sealed"), Unit(b, "sealed"), new { skuId = Guid.NewGuid(), outcome = "sealed" }, Loose("sealed") }, // another sku
            new[] { Unit(a, "sealed"), Unit(b, "broken"), Loose("sealed"), Loose("sealed") },                 // not an outcome
            Array.Empty<object>(),
        })
        {
            using var response = await RtsAsync(t, f.ShipmentId, units);
            await response.ExpectValidationAsync("units");
        }
        using (var noKey = await t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/{f.ShipmentId}/returned-to-sender", new { units = new[] { Unit(a, "sealed") } }, Warehouse))
            await noKey.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REQUIRED");
        Assert.Equal(FailureStatuses.Confirmed, (await FailureAsync(t, f.ShipmentId)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));

        using var ok = await RtsAsync(t, f.ShipmentId, [Unit(a, "sealed"), Unit(b, "failed"), Loose("sealed"), Loose("sealed")]);
        await ok.OkJsonAsync();
        var units2 = Assert.Single(await Outbox.PayloadsAsync<ReceiveReturnedToSenderV1>(t, FulfillmentMessages.ReceiveReturnedToSender)).Units;
        Assert.Equal((loose, null, 2, "sealed"), units2.Where(u => u.Serial is null).Select(u => (u.SkuId, u.Serial, u.Quantity, u.Outcome)).Single()); // grouped by (sku, outcome)
        Assert.Equal(2, units2.Count(u => u.Serial is not null));
        await using var db = t.NewDb();
        Assert.Contains("\"serial\": null", await db.Database.SqlQuery<string>($"SELECT payload AS \"Value\" FROM outbox_messages WHERE message_type = {FulfillmentMessages.ReceiveReturnedToSender}").SingleAsync(Ct));
    }

    [Fact]
    public async Task Wrong_states_and_bad_input()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var lost = await PossiblyLostAsync(t);
        var returned = await ReturnedAsync(t);
        var handed = await HandedOverAsync(t);
        var one = new[] { Unit(lost.Serials[0], "sealed"), Unit(lost.Serials[1], "sealed") };

        using (var rtsOnLost = await RtsAsync(t, lost.ShipmentId, one)) await rtsOnLost.ExpectErrorAsync(400, "INVALID_STATE");
        using (var rtsOnHandedOver = await RtsAsync(t, handed.ShipmentId, one)) await rtsOnHandedOver.ExpectErrorAsync(400, "INVALID_STATE");
        using (var lostOnReturned = await LostAsync(t, returned.ShipmentId, "refund")) await lostOnReturned.ExpectErrorAsync(400, "INVALID_STATE");
        using (var reshipOnReturned = await LostAsync(t, returned.ShipmentId, "reship")) await reshipOnReturned.ExpectErrorAsync(400, "INVALID_STATE");
        using (var lostOnHandedOver = await LostAsync(t, handed.ShipmentId, "refund")) await lostOnHandedOver.ExpectErrorAsync(400, "INVALID_STATE");
        using (var unknown = await LostAsync(t, Guid.NewGuid(), "refund")) await unknown.ExpectErrorAsync(404, "NOT_FOUND");
        using (var unknownRts = await RtsAsync(t, Guid.NewGuid(), one)) await unknownRts.ExpectErrorAsync(404, "NOT_FOUND");
        using (var badResolution = await LostAsync(t, lost.ShipmentId, "keep")) await badResolution.ExpectValidationAsync("resolution");
        using (var badId = await t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/nope/lost", new { resolution = "refund" }, Support, Guid.NewGuid())) await badId.ExpectValidationAsync("shipment_id");
        using (var noKey = await t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/{lost.ShipmentId}/lost", new { resolution = "refund" }, Support))
            await noKey.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REQUIRED");
        Assert.Equal(ShipmentStatuses.PossiblyLost, (await ShipmentAsync(t, lost.ShipmentId)).Status);

        await LostOkAsync(t, lost.ShipmentId, "refund");
        using (var again = await LostAsync(t, lost.ShipmentId, "refund")) await again.ExpectErrorAsync(400, "INVALID_STATE");
        using (var reshipAfter = await LostAsync(t, lost.ShipmentId, "reship")) await reshipAfter.ExpectErrorAsync(400, "INVALID_STATE");
        Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Equal(0, t.Inventory.Count("CommitForReship")); // a wrong state is found before inventory is called
    }

    [Fact]
    public async Task Confirm_lost_refund()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await PossiblyLostAsync(t);

        var body = await LostOkAsync(t, f.ShipmentId, "refund");

        Assert.Equal(("lost", "resolved_refund"), (body.GetProperty("kind").GetString(), body.GetProperty("status").GetString()));
        Assert.Equal(ShipmentStatuses.Lost, (await ShipmentAsync(t, f.ShipmentId)).Status);
        Assert.Equal(f.Order.OrderId, Assert.Single(await Outbox.PayloadsAsync<MarkLostInTransitV1>(t, FulfillmentMessages.MarkLostInTransit)).RefId);
        var refund = Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Equal((f.Order.OrderId, "delivery_failure", f.Order.TotalCents), (refund.OrderId, refund.Reason, refund.AmountCents));
        Assert.Equal([(1, 2002L), (2, 9900L)], CommandLines(refund));
        Assert.All(await LinesAsync(t, f.Order.OrderId), l => Assert.Equal((l.Quantity, l.PaidCents), (l.RefundedUnits, l.RefundedCents)));
        Assert.All(await CoveragesAsync(t, f.Order.OrderId), c => Assert.Equal(CoverageStatuses.Cancelled, c.Status));
        Assert.Equal(new DeliveryFailedV1(f.Order.OrderId, "lost"), Assert.Single(await Outbox.PayloadsAsync<DeliveryFailedV1>(t, FulfillmentMessages.DeliveryFailed)));
        var failure = await FailureAsync(t, f.ShipmentId);
        Assert.Equal((FailureStatuses.ResolvedRefund, Support.Id, null), (failure.Status, failure.DecidedBy, failure.ReshipId));
        var saga = Assert.Single(await SagasAsync(t, f.ShipmentId));
        Assert.Equal((SagaTypes.DeliveryFailure, refund.RefundId), (saga.Type, saga.RefundId));
        var audit = Assert.Single(await Outbox.AuditAsync(t), a => a.Action == "fulfillment.confirm-lost");
        Assert.Equal(("fulfillment.confirm-lost", f.ShipmentId.ToString(), "refund"), (audit.Action, audit.TargetId, audit.Details.GetProperty("resolution").GetString()));
        Assert.Empty(await ReshipTasksAsync(t, f.Order.OrderId));
    }

    [Fact]
    public async Task F13_confirm_lost_reship_with_stock()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await PossiblyLostAsync(t);
        var reshipId = Ids.ReshipId(f.ShipmentId);

        var body = await LostOkAsync(t, f.ShipmentId, "reship");

        Assert.Equal(("lost", "resolved_reship", reshipId.ToString()), (body.GetProperty("kind").GetString(), body.GetProperty("status").GetString(), body.GetProperty("reshipId").GetString()));
        var call = Assert.Single(t.Inventory.Calls, c => c.Method == "CommitForReship");
        Assert.Equal(Support.Id.ToString(), call.UserId); // R11: the staff caller's headers reach inventory through the guard's provider
        var commit = Assert.IsType<CommitForReshipRequest>(call.Request);
        Assert.Equal((reshipId.ToString(), f.Order.OrderId.ToString()), (commit.ReshipId, commit.OrderId));
        var device = f.Order.Lines.Single(l => l.LineNo == 1);
        var line = Assert.Single(commit.Lines); // product lines only
        Assert.Equal((device.SkuId.ToString(), 2), (line.SkuId, line.Quantity));

        Assert.Empty(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Empty(await Outbox.PayloadsAsync<DeliveryFailedV1>(t, FulfillmentMessages.DeliveryFailed));
        Assert.Equal(f.Order.OrderId, Assert.Single(await Outbox.PayloadsAsync<MarkLostInTransitV1>(t, FulfillmentMessages.MarkLostInTransit)).RefId);
        Assert.Equal(ShipmentStatuses.Lost, (await ShipmentAsync(t, f.ShipmentId)).Status);
        var failure = await FailureAsync(t, f.ShipmentId);
        Assert.Equal((FailureStatuses.ResolvedReship, reshipId, Support.Id), (failure.Status, failure.ReshipId, failure.DecidedBy));

        await using var db = t.NewDb();
        var original = await db.PackTasks.AsNoTracking().SingleAsync(p => p.Id == f.TaskId, Ct);
        var reship = Assert.Single(await ReshipTasksAsync(t, f.Order.OrderId));
        Assert.Equal((reshipId, TaskStatuses.Pending, original.ShipTo, original.ShippingOption), (reship.RefId, reship.Status, reship.ShipTo, reship.ShippingOption));
        var lines = (l: await db.PackTaskLines.AsNoTracking().Where(x => x.TaskId == reship.Id).OrderBy(x => x.LineNo).ToListAsync(Ct), o: await db.PackTaskLines.AsNoTracking().Where(x => x.TaskId == f.TaskId).OrderBy(x => x.LineNo).ToListAsync(Ct));
        Assert.Equal(lines.o.Select(x => (x.LineNo, x.Kind, x.SkuId, x.Serialized, x.Quantity, x.ParentLineNo, x.CoverageMonths)), lines.l.Select(x => (x.LineNo, x.Kind, x.SkuId, x.Serialized, x.Quantity, x.ParentLineNo, x.CoverageMonths)));

        // Pack the reship with new serials: the coverage moves to the serial of the same rank (the first one) and no coverage is added.
        var before = Assert.Single(await CoveragesAsync(t, f.Order.OrderId));
        Assert.Equal((CoverageStatuses.PendingDelivery, f.Serials[0]), (before.Status, before.Serial));
        var fresh = new[] { "RSB" + f.Serials[0][3..], "RSA" + f.Serials[0][3..] };
        var task = $"{Admin}/tasks/{reship.Id}";
        using (var start = await t.SendAsync(HttpMethod.Post, $"{task}/start", new { version = reship.Version }, Warehouse)) await start.OkJsonAsync();
        foreach (var serial in fresh)
            using (var scan = await t.SendAsync(HttpMethod.Post, $"{task}/serials", new { lineNo = 1, serial }, Warehouse)) await scan.OkJsonAsync();
        using (var complete = await t.SendAsync(HttpMethod.Post, $"{task}/complete", new { version = reship.Version + 1 }, Warehouse)) await complete.OkJsonAsync();
        var moved = Assert.Single(await CoveragesAsync(t, f.Order.OrderId));
        Assert.Equal((before.Id, CoverageStatuses.PendingDelivery, fresh[0]), (moved.Id, moved.Status, moved.Serial));

        // Deliver it: the order is delivered, the coverage active, and shipment-delivered names the reship.
        await Seed.RunLabelWorkerAsync(t);
        var reshipShipment = await db.Shipments.AsNoTracking().SingleAsync(s => s.PackTaskId == reship.Id, Ct);
        using (var handover = await t.SendAsync(HttpMethod.Post, $"{Admin}/handover", new { shipmentId = reshipShipment.Id }, Warehouse)) await handover.OkJsonAsync();
        var tracking = (await ShipmentAsync(t, reshipShipment.Id)).TrackingNumber!;
        t.Clock.Advance(TimeSpan.FromDays(3));
        await Seed.TrackAsync(t, tracking, "DELIVERED");

        var delivered = Assert.Single(await Outbox.PayloadsAsync<ShipmentDeliveredV1>(t, FulfillmentMessages.ShipmentDelivered));
        Assert.Equal((f.Order.OrderId, reshipId, t.Clock.GetUtcNow()), (delivered.OrderId, delivered.RefId, delivered.DeliveredAt));
        var active = Assert.Single(await CoveragesAsync(t, f.Order.OrderId));
        Assert.Equal((CoverageStatuses.Active, fresh[0], t.Clock.GetUtcNow()), (active.Status, active.Serial, active.StartsAt));
        Assert.Equal(t.Clock.GetUtcNow(), (await db.OrderAccounts.AsNoTracking().SingleAsync(Ct)).DeliveredAt);
        Assert.Equal(ShipmentStatuses.Lost, (await ShipmentAsync(t, f.ShipmentId)).Status);
    }

    /// <summary>Starts, scans <paramref name="serials"/> onto device line 1 in order, completes, buys the label and hands the reship over.</summary>
    private static async Task<(Guid ShipmentId, string Tracking)> PackAndHandOverAsync(TestFulfillment t, PackTask reship, params string[] serials)
    {
        var task = $"{Admin}/tasks/{reship.Id}";
        using (var start = await t.SendAsync(HttpMethod.Post, $"{task}/start", new { version = reship.Version }, Warehouse)) await start.OkJsonAsync();
        foreach (var serial in serials)
            using (var scan = await t.SendAsync(HttpMethod.Post, $"{task}/serials", new { lineNo = 1, serial }, Warehouse)) await scan.OkJsonAsync();
        using (var complete = await t.SendAsync(HttpMethod.Post, $"{task}/complete", new { version = reship.Version + 1 }, Warehouse)) await complete.OkJsonAsync();
        await Seed.RunLabelWorkerAsync(t);
        await using var db = t.NewDb();
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(s => s.PackTaskId == reship.Id, Ct);
        using (var handover = await t.SendAsync(HttpMethod.Post, $"{Admin}/handover", new { shipmentId = shipment.Id }, Warehouse)) await handover.OkJsonAsync();
        return (shipment.Id, (await ShipmentAsync(t, shipment.Id)).TrackingNumber!);
    }

    [Fact]
    public async Task A_second_reship_moves_every_coverage_again_by_rank()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await PossiblyLostAsync(t, b => b.Line(2, "warranty_plan", quantity: 2, parent: 1, months: 12, paid: 9900, tax: 800)); // a plan on both devices
        var covered = (await CoveragesAsync(t, f.Order.OrderId)).ToDictionary(c => c.Serial, c => c.Id);
        Assert.Equal(f.Serials.Order(), covered.Keys.Order());
        var suffix = f.Serials[0][3..];

        await LostOkAsync(t, f.ShipmentId, "reship");
        var first = Assert.Single(await ReshipTasksAsync(t, f.Order.OrderId));
        var (firstShipment, tracking) = await PackAndHandOverAsync(t, first, "RAA" + suffix, "RAB" + suffix);
        await Seed.TrackAsync(t, tracking, "FAILURE");
        await LostOkAsync(t, firstShipment, "reship");
        var second = Assert.Single(await ReshipTasksAsync(t, f.Order.OrderId), p => p.Id != first.Id);
        Assert.Equal(Ids.ReshipId(firstShipment), second.RefId);
        await PackAndHandOverAsync(t, second, "RBA" + suffix, "RBB" + suffix);

        var now = (await CoveragesAsync(t, f.Order.OrderId)).ToDictionary(c => c.Id, c => c.Serial);
        Assert.Equal(("RBA" + suffix, "RBB" + suffix), (now[covered[f.Serials[0]]], now[covered[f.Serials[1]]])); // rank 0 stays rank 0, rank 1 stays rank 1
        Assert.All(await CoveragesAsync(t, f.Order.OrderId), c => Assert.Equal(CoverageStatuses.PendingDelivery, c.Status));
        Assert.Equal(2, (await ReshipTasksAsync(t, f.Order.OrderId)).Count);
    }

    [Fact]
    public async Task Reship_without_stock_is_RESHIP_NO_STOCK()   // §8
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await PossiblyLostAsync(t);
        var key = Guid.NewGuid();
        t.Inventory.SoldOut = true;

        using (var response = await LostAsync(t, f.ShipmentId, "reship", key)) await response.ExpectErrorAsync(400, "RESHIP_NO_STOCK");

        Assert.Empty(await ReshipTasksAsync(t, f.Order.OrderId));
        Assert.Equal(FailureStatuses.PossiblyLost, (await FailureAsync(t, f.ShipmentId)).Status);
        await using (var db = t.NewDb())
            Assert.Equal(0, await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM idempotency_keys WHERE key = {key.ToString()}").SingleAsync(Ct)); // released, not stored

        var body = await LostOkAsync(t, f.ShipmentId, "refund");
        Assert.Equal("resolved_refund", body.GetProperty("status").GetString());
        Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
    }

    [Fact]
    public async Task Confirm_lost_retried_with_the_same_key()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await PossiblyLostAsync(t);
        var key = Guid.NewGuid();

        var first = await LostOkAsync(t, f.ShipmentId, "reship", key);
        var second = await LostOkAsync(t, f.ShipmentId, "reship", key);

        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Single(await ReshipTasksAsync(t, f.Order.OrderId));
        Assert.Equal(1, t.Inventory.Count("CommitForReship"));
        Assert.Single(await Outbox.PayloadsAsync<MarkLostInTransitV1>(t, FulfillmentMessages.MarkLostInTransit));
        using var other = await LostAsync(t, f.ShipmentId, "refund", key); // same key, other request
        await other.ExpectErrorAsync(400, "IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task Reship_is_refused_when_the_failure_changed_during_the_inventory_call()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await PossiblyLostAsync(t);
        t.Inventory.Before = async () =>   // the carrier delivers while inventory is committing the reship
        {
            await Seed.TrackAsync(t, f.Tracking, "DELIVERED");
        };

        using (var response = await LostAsync(t, f.ShipmentId, "reship")) await response.ExpectErrorAsync(400, "INVALID_STATE");
        t.Inventory.Before = null;

        Assert.Equal(1, t.Inventory.Count("CommitForReship"));
        Assert.Empty(await ReshipTasksAsync(t, f.Order.OrderId));
        Assert.Equal(ShipmentStatuses.Delivered, (await ShipmentAsync(t, f.ShipmentId)).Status);
        Assert.Equal(FailureStatuses.Dismissed, (await FailureAsync(t, f.ShipmentId)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<MarkLostInTransitV1>(t, FulfillmentMessages.MarkLostInTransit));
    }

    [Fact]
    public async Task Dismiss_restores_the_previous_status()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var f = await PossiblyLostAsync(t, inTransit: true);
        Assert.Equal((ShipmentStatuses.PossiblyLost, ShipmentStatuses.InTransit), ((await ShipmentAsync(t, f.ShipmentId)).Status, (await FailureAsync(t, f.ShipmentId)).PreviousStatus));

        using (var response = await t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/{f.ShipmentId}/dismiss", new { }, Support))
        {
            var body = await response.OkJsonAsync();
            Assert.Equal(("dismissed", "in_transit"), (body.GetProperty("status").GetString(), body.GetProperty("previousStatus").GetString()));
        }

        Assert.Equal(ShipmentStatuses.InTransit, (await ShipmentAsync(t, f.ShipmentId)).Status);
        var failure = await FailureAsync(t, f.ShipmentId);
        Assert.Equal((FailureStatuses.Dismissed, t.Clock.GetUtcNow()), (failure.Status, failure.DismissedAt));
        using (var again = await t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/{f.ShipmentId}/dismiss", new { }, Support)) await again.ExpectErrorAsync(400, "INVALID_STATE");
        var audit = Assert.Single(await Outbox.AuditAsync(t), a => a.Action == "fulfillment.dismiss-possibly-lost");
        Assert.Equal(("fulfillment.dismiss-possibly-lost", f.ShipmentId.ToString()), (audit.Action, audit.TargetId));

        var handed = await HandedOverAsync(t);
        using (var noFailure = await t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/{handed.ShipmentId}/dismiss", new { }, Support)) await noFailure.ExpectErrorAsync(400, "INVALID_STATE");
        using (var unknown = await t.SendAsync(HttpMethod.Post, $"{Admin}/shipments/{Guid.NewGuid()}/dismiss", new { }, Support)) await unknown.ExpectErrorAsync(404, "NOT_FOUND");
    }

    [Fact]
    public async Task Failure_lists_and_roles()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var lost = await PossiblyLostAsync(t);
        t.Clock.Advance(TimeSpan.FromMinutes(1));
        var returned = await ReturnedAsync(t);

        async Task<JsonElement[]> ListAsync(string query, TestCaller? caller = null)
        {
            using var response = await t.SendAsync(HttpMethod.Get, $"{Admin}/failures{query}", caller: caller ?? Support);
            return (await response.OkJsonAsync()).GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToArray();
        }

        var all = await ListAsync("");
        Assert.Equal([lost.ShipmentId.ToString(), returned.ShipmentId.ToString()], all.Select(e => e.GetProperty("shipmentId").GetString())); // oldest first
        Assert.Equal(("lost", "possibly_lost", "handed_over", lost.Order.OrderId.ToString()),
            (all[0].GetProperty("kind").GetString(), all[0].GetProperty("status").GetString(), all[0].GetProperty("previousStatus").GetString(), all[0].GetProperty("orderId").GetString()));
        Assert.Equal(("returned_to_sender", "confirmed"), (all[1].GetProperty("kind").GetString(), all[1].GetProperty("status").GetString()));
        Assert.Equal(lost.ShipmentId.ToString(), Assert.Single(await ListAsync("?status=possibly_lost")).GetProperty("shipmentId").GetString());
        Assert.Equal(returned.ShipmentId.ToString(), Assert.Single(await ListAsync("?status=confirmed", TestCaller.Staff("admin"))).GetProperty("shipmentId").GetString());
        Assert.Empty(await ListAsync("?status=dismissed"));
        Assert.Single(await ListAsync("?pageSize=1&page=2"));
        using (var bad = await t.SendAsync(HttpMethod.Get, $"{Admin}/failures?status=nope", caller: Support)) await bad.ExpectValidationAsync("status");
        using (var big = await t.SendAsync(HttpMethod.Get, $"{Admin}/failures?pageSize=101", caller: Support)) await big.ExpectValidationAsync("page_size");

        // Roles come before the key and the body.
        var units = new[] { Unit("X1", "sealed") };
        foreach (var (method, path, body, caller) in new (HttpMethod, string, object?, TestCaller)[]
        {
            (HttpMethod.Get, "/failures", null, Warehouse),
            (HttpMethod.Post, $"/shipments/{lost.ShipmentId}/lost", new { resolution = "refund" }, Warehouse),
            (HttpMethod.Post, $"/shipments/{lost.ShipmentId}/dismiss", new { }, Warehouse),
            (HttpMethod.Post, $"/shipments/{returned.ShipmentId}/returned-to-sender", new { units }, Support),
            (HttpMethod.Post, $"/shipments/{returned.ShipmentId}/returned-to-sender", new { units }, TestCaller.Customer()),
        })
        {
            using var response = await t.SendAsync(method, Admin + path, body, caller);
            await response.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        }
        Assert.Equal(FailureStatuses.PossiblyLost, (await FailureAsync(t, lost.ShipmentId)).Status);
    }

    [Fact]
    public void Reship_id_is_a_name_based_uuid_v5()
    {
        // RFC 9562 appendix A.4: UUIDv5 of "www.example.com" in the DNS namespace.
        Assert.Equal(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"), Ids.NameBased(Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com"));

        var shipment = Guid.NewGuid();
        Assert.Equal(Ids.ReshipId(shipment), Ids.ReshipId(shipment));
        Assert.NotEqual(Ids.ReshipId(shipment), Ids.ReshipId(Guid.NewGuid()));
        Assert.Equal(5, Ids.ReshipId(shipment).Version);
    }
}
