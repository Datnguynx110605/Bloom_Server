using System.Net;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Tests;

public class HandoverTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TestCaller Warehouse = TestCaller.Staff("warehouse");
    private const string Path = "/api/admin/fulfillment/handover";

    private static Task<HttpResponseMessage> HandOverAsync(TestFulfillment t, object body, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, Path, body, caller ?? Warehouse);

    private static async Task<string> TaskStatusAsync(TestFulfillment t, Guid taskId)
    {
        await using var db = t.NewDb();
        return await db.PackTasks.AsNoTracking().Where(p => p.Id == taskId).Select(p => p.Status).SingleAsync(Ct);
    }

    private static async Task<(Bloom.Order.Contracts.OrderReadyToFulfillV1 Order, Guid TaskId, Guid ShipmentId)> LabelReadyAsync(TestFulfillment t, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(); // an open coverage keeps its serial, so each round needs fresh ones (F01)
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        var (taskId, shipmentId) = await Seed.LabelReadyAsync(t, ready, $"SNB{id}", $"SNA{id}");
        return (ready, taskId, shipmentId);
    }

    [Fact]
    public async Task Handover_by_tracking_number_ships_the_task()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        const string id = "X1";
        var (order, taskId, shipmentId) = await LabelReadyAsync(t, id);
        var tracking = (await Seed.ShipmentAsync(t, shipmentId)).TrackingNumber!;
        t.Clock.Advance(TimeSpan.FromSeconds(90.5));

        using var response = await HandOverAsync(t, new { trackingNumber = tracking });
        var body = await response.OkJsonAsync();

        Assert.Equal((shipmentId.ToString(), "handed_over", tracking), (body.GetProperty("id").GetString(), body.GetProperty("status").GetString(), body.GetProperty("trackingNumber").GetString()));
        var shipment = await Seed.ShipmentAsync(t, shipmentId);
        Assert.Equal(ShipmentStatuses.HandedOver, shipment.Status);
        Assert.Equal(t.Clock.GetUtcNow(), shipment.HandedOverAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(TaskStatuses.HandedOver, await TaskStatusAsync(t, taskId));
        var shipped = Assert.Single(await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped));
        Assert.Equal((order.OrderId, order.OrderId, tracking), (shipped.OrderId, shipped.RefId, shipped.TrackingNumber));
        var line = Assert.Single(shipped.Lines); // the plan line is left out
        Assert.Equal((order.Lines[0].SkuId, 2), (line.SkuId, line.Quantity));
        Assert.Equal([$"SNB{id}", $"SNA{id}"], line.Serials);
        var audit = (await Outbox.AuditAsync(t)).Last();
        Assert.Equal(("fulfillment.hand-over", "shipment", shipmentId.ToString(), Warehouse.Id), (audit.Action, audit.TargetType, audit.TargetId, audit.ActorId));
        Assert.Equal(1, metrics.Count("time_to_handover_seconds", ("shipping_option", "standard")));
        Assert.Equal(90, metrics.Sum("time_to_handover_seconds", ("shipping_option", "standard")));
    }

    [Fact]
    public async Task Handover_by_shipment_id()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, taskId, shipmentId) = await LabelReadyAsync(t);

        using var response = await HandOverAsync(t, new { shipmentId });
        await response.OkJsonAsync();

        Assert.Equal(ShipmentStatuses.HandedOver, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Equal(TaskStatuses.HandedOver, await TaskStatusAsync(t, taskId));
    }

    [Fact]
    public async Task Double_scan_returns_the_shipment_unchanged()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (_, _, shipmentId) = await LabelReadyAsync(t);
        using var first = await HandOverAsync(t, new { shipmentId });
        var firstBody = await first.OkJsonAsync();
        var audits = (await Outbox.AuditAsync(t)).Count;
        t.Clock.Advance(TimeSpan.FromMinutes(5));

        using var second = await HandOverAsync(t, new { shipmentId });

        Assert.Equal(firstBody.ToString(), (await second.OkJsonAsync()).ToString());
        Assert.Single(await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped));
        Assert.Equal(audits, (await Outbox.AuditAsync(t)).Count);
        Assert.Equal(1, metrics.Count("time_to_handover_seconds"));
    }

    [Fact]
    public async Task Double_scan_at_the_same_instant_ships_once()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, _, shipmentId) = await LabelReadyAsync(t);

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => HandOverAsync(t, new { shipmentId })));

        foreach (var r in responses) { Assert.Equal("handed_over", (await r.OkJsonAsync()).GetProperty("status").GetString()); r.Dispose(); }
        Assert.Single(await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped));
    }

    [Fact]
    public async Task Handover_before_label_is_LABEL_NOT_READY()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(t, ready);
        var (taskId, shipmentId) = await Seed.PackedAsync(t, ready, "SN1", "SN2");

        using var response = await HandOverAsync(t, new { shipmentId });
        await response.ExpectErrorAsync(400, "LABEL_NOT_READY");

        Assert.Equal(TaskStatuses.Packed, await TaskStatusAsync(t, taskId)); // rolled back
        Assert.Equal(ShipmentStatuses.LabelPending, (await Seed.ShipmentAsync(t, shipmentId)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped));
    }

    [Fact]
    public async Task Handover_of_cancelled_task_is_INVALID_STATE()  // FR-01
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (order, taskId, shipmentId) = await LabelReadyAsync(t);
        await t.Internal.CancelPackTaskAsync(new() { OrderId = order.OrderId.ToString("D"), By = "support" }, cancellationToken: Ct).ResponseAsync;

        using var response = await HandOverAsync(t, new { shipmentId });
        await response.ExpectErrorAsync(400, "INVALID_STATE");

        Assert.Equal(TaskStatuses.Cancelled, await TaskStatusAsync(t, taskId));
        Assert.Empty(await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped));
    }

    [Fact]
    public async Task F01_support_cancel_and_handover_at_the_same_instant()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var handovers = 0;
        for (var round = 0; round < 30; round++)
        {
            var (order, taskId, shipmentId) = await LabelReadyAsync(t);

            var cancel = Task.Run(async () =>
            {
                try { await t.Internal.CancelPackTaskAsync(new() { OrderId = order.OrderId.ToString("D"), By = "support" }, cancellationToken: Ct).ResponseAsync; return null; }
                catch (RpcException e) { return Rpc.Info(e).Reason; }
            }, Ct);
            var handover = Task.Run(async () =>
            {
                using var response = await HandOverAsync(t, new { shipmentId });
                return response.StatusCode == HttpStatusCode.OK ? null : (await response.ExpectErrorAsync(400, "INVALID_STATE")).GetProperty("reason").GetString();
            }, Ct);
            await Task.WhenAll(cancel, handover);

            if (cancel.Result is null)
            {
                Assert.Equal("INVALID_STATE", handover.Result);
                Assert.Equal(TaskStatuses.Cancelled, await TaskStatusAsync(t, taskId));
                Assert.NotEqual(ShipmentStatuses.HandedOver, (await Seed.ShipmentAsync(t, shipmentId)).Status);
            }
            else
            {
                handovers++;
                Assert.Equal("ALREADY_SHIPPED", cancel.Result);
                Assert.Null(handover.Result);
                Assert.Equal(TaskStatuses.HandedOver, await TaskStatusAsync(t, taskId));
                Assert.Equal(ShipmentStatuses.HandedOver, (await Seed.ShipmentAsync(t, shipmentId)).Status);
            }
        }
        Assert.Equal(handovers, (await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped)).Count);
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("40P01") || e.Contains("deadlock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Handover_target_and_role_are_checked()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var (_, _, shipmentId) = await LabelReadyAsync(t);

        using (var neither = await HandOverAsync(t, new { })) await neither.ExpectValidationAsync("target");
        using (var badId = await HandOverAsync(t, new { shipmentId = "x" })) await badId.ExpectValidationAsync("shipment_id");
        using (var unknownId = await HandOverAsync(t, new { shipmentId = Guid.NewGuid() })) await unknownId.ExpectErrorAsync(404, "NOT_FOUND");
        using (var unknownTracking = await HandOverAsync(t, new { trackingNumber = "NOPE" })) await unknownTracking.ExpectErrorAsync(404, "NOT_FOUND");
        using (var support = await HandOverAsync(t, new { shipmentId }, TestCaller.Staff("support"))) await support.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        Assert.Equal(ShipmentStatuses.LabelReady, (await Seed.ShipmentAsync(t, shipmentId)).Status);
    }

    private static async Task<Guid> SeedShipmentAsync(TestFulfillment t, string direction, string tracking)
    {
        await using var db = t.NewDb();
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(), OrderId = Guid.NewGuid(), ReturnRequestId = Guid.NewGuid(), Direction = direction, Service = ShippingOptions.Standard,
            Status = ShipmentStatuses.LabelReady, TrackingNumber = tracking, NextAttemptAt = t.Clock.GetUtcNow(), CreatedAt = t.Clock.GetUtcNow(),
        };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync(Ct);
        return shipment.Id;
    }

    [Fact]
    public async Task Ship_back_handover_moves_only_the_shipment()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var id = await SeedShipmentAsync(t, Directions.ShipBack, "SB1");

        using var response = await HandOverAsync(t, new { trackingNumber = "SB1" });
        await response.OkJsonAsync();

        Assert.Equal(ShipmentStatuses.HandedOver, (await Seed.ShipmentAsync(t, id)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<ShipmentShippedV1>(t, FulfillmentMessages.ShipmentShipped));
        Assert.Equal("fulfillment.hand-over", (await Outbox.AuditAsync(t)).Last().Action);
    }

    [Fact]
    public async Task Return_shipment_cannot_be_handed_over()
    {
        await using var t = await TestFulfillment.StartAsync(containers);
        var id = await SeedShipmentAsync(t, Directions.Return, "RT1");

        using var response = await HandOverAsync(t, new { shipmentId = id });
        await response.ExpectErrorAsync(400, "INVALID_STATE");

        Assert.Equal(ShipmentStatuses.LabelReady, (await Seed.ShipmentAsync(t, id)).Status);
    }
}
