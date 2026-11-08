using System.Text.Json;
using Bloom.Catalog.Contracts;
using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Consumers;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderLine = Bloom.Order.Data.OrderLine;

namespace Bloom.Order.Tests;

public class FulfillmentEventTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Task PackingStartedAsync(TestOrder t, Guid id) => Consume.Async<PackingStartedConsumer>(t, FulfillmentMessages.PackingStarted, new PackingStartedV1(id));

    private static Task ShippedAsync(TestOrder t, Guid id, Guid? refId = null) =>
        Consume.Async<ShipmentShippedConsumer>(t, FulfillmentMessages.ShipmentShipped, new ShipmentShippedV1(id, refId ?? id, [], "TRACK-1"));

    private static Task DeliveredAsync(TestOrder t, Guid id, DateTimeOffset at, Guid? refId = null) =>
        Consume.Async<ShipmentDeliveredConsumer>(t, FulfillmentMessages.ShipmentDelivered, new ShipmentDeliveredV1(id, refId ?? id, at));

    private static Task DecidedAsync(TestOrder t, Guid id, params (int LineNo, int Quantity)[] accepted) =>
        Consume.Async<ReturnDecidedConsumer>(t, FulfillmentMessages.ReturnDecided,
            new ReturnDecidedV1(id, Guid.NewGuid(), [.. accepted.Select(a => new AcceptedLineV1(a.LineNo, a.Quantity))], 0));

    private static Task RefundCompletedAsync(TestOrder t, Guid id, params (int LineNo, long Cents)[] lines) =>
        Consume.Async<RefundCompletedConsumer>(t, PaymentMessages.RefundCompleted,
            new RefundCompletedV1(id, Guid.NewGuid(), lines.Sum(l => l.Cents), [.. lines.Select(l => new RefundLineV1(l.LineNo, l.Cents))]));

    /// <summary>A paid regular order: line 1 a device x2, line 2 its plan (x1), optionally a second device as line 3 and a 10% coupon.</summary>
    private static async Task<(Guid Account, Guid OrderId, SkuPricing[] Devices)> PaidAsync(TestOrder t, bool coupon = false, bool secondDevice = false)
    {
        var account = Guid.NewGuid();
        if (coupon) await t.Rpc.Staff.CreateCouponAsync(new CreateCouponRequest { Code = "SAVE-10", Type = "percent", Value = 10, Scope = "order" }, TestCaller.Staff("catalog_manager").Call());
        var device = Seed.Device(t.Upstreams, 1000);
        var plan = Seed.Plan(t.Upstreams, device, 9900);
        var other = secondDevice ? Seed.Device(t.Upstreams, 2000) : null;
        var lines = new List<(SkuPricing, int, int?)> { (device, 2, null), (plan, 1, 1) };
        if (other is not null) lines.Add((other, 1, null));
        await Seed.CartAsync(t, account, [.. lines]);
        var response = await Seed.CheckoutAsync(t, account, configure: r => { if (coupon) r.CouponCode = "SAVE-10"; });
        var orderId = Guid.Parse(response.OrderId);
        await Consume.Async<PaymentSucceededConsumer>(t, PaymentMessages.Succeeded,
            new PaymentSucceededV1(orderId, FakeUpstreams.PaymentIdOf(response.OrderId), 0, t.Clock.GetUtcNow()));
        return (account, orderId, other is null ? [device] : [device, other]);
    }

    private static async Task<OrderRow> OrderAsync(TestOrder t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id, Ct);
    }

    private static async Task<List<OrderStatusHistory>> HistoryAsync(TestOrder t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == id).OrderBy(h => h.Id).ToListAsync(Ct);
    }

    private static async Task<List<OrderLine>> LinesAsync(TestOrder t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.OrderLines.AsNoTracking().Where(l => l.OrderId == id).OrderBy(l => l.LineNo).ToListAsync(Ct);
    }

    private static async Task SetStatusAsync(TestOrder t, Guid id, string status)
    {
        await using var db = t.NewDb();
        await db.Database.ExecuteSqlAsync($"UPDATE orders SET status = {status} WHERE id = {id}", Ct);
    }

    /// <summary>A paid order cancelled by its customer: the cancellation saga waits for <c>inventory_release</c> and <c>refund</c>.</summary>
    private static async Task<Guid> CancelledAsync(TestOrder t)
    {
        var (account, orderId, _) = await PaidAsync(t);
        var order = await OrderAsync(t, orderId);
        await t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId.ToString("D"), Version = order.Version }, TestCaller.Customer(account).Call());
        return orderId;
    }

    private static async Task<OrderSaga> SagaAsync(TestOrder t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Set<OrderSaga>().AsNoTracking().SingleAsync(s => s.OrderId == id && s.Type == SagaTypes.Cancellation, Ct);
    }

    [Fact]
    public async Task Lifecycle_paid_packing_shipped_delivered()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, orderId, devices) = await PaidAsync(t, secondDevice: true);
        var deliveredAt = new DateTimeOffset(2026, 11, 5, 10, 30, 0, TimeSpan.Zero);

        await PackingStartedAsync(t, orderId);
        Assert.Equal(OrderStatuses.Packing, (await OrderAsync(t, orderId)).Status);
        await ShippedAsync(t, orderId);
        Assert.Equal(OrderStatuses.Shipped, (await OrderAsync(t, orderId)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<OrderDeliveredV1>(t, OrderMessages.Delivered));
        await DeliveredAsync(t, orderId, deliveredAt);

        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.Delivered, deliveredAt), (order.Status, order.DeliveredAt));
        var steps = (await HistoryAsync(t, orderId)).TakeLast(3).Select(h => (h.FromStatus, h.ToStatus, h.ActorType, h.ActorId)).ToList();
        Assert.Equal([("paid", "packing", "system", (Guid?)null), ("packing", "shipped", "system", null), ("shipped", "delivered", "system", null)], steps);
        var delivered = Assert.Single(await Outbox.PayloadsAsync<OrderDeliveredV1>(t, OrderMessages.Delivered));
        Assert.Equal((orderId, account, deliveredAt), (delivered.OrderId, delivered.AccountId, delivered.DeliveredAt));
        Assert.Equal(devices.Select(d => Guid.Parse(d.ProductId)).Order(), delivered.ProductIds.Order()); // product lines only, each product once
    }

    [Fact]
    public async Task Reordered_or_reship_events_are_no_ops()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (_, orderId, _) = await PaidAsync(t);
        var at = t.Clock.GetUtcNow();

        await ShippedAsync(t, orderId); // shipped before packing: no-op
        await DeliveredAsync(t, orderId, at);
        var paid = await OrderAsync(t, orderId);
        Assert.Equal(OrderStatuses.Paid, paid.Status);
        await PackingStartedAsync(t, Guid.NewGuid()); // unknown order: no-op, no error

        await PackingStartedAsync(t, orderId);
        await ShippedAsync(t, orderId);
        var shipped = await OrderAsync(t, orderId);
        var history = (await HistoryAsync(t, orderId)).Count;
        await ShippedAsync(t, orderId, refId: Guid.NewGuid()); // a reship's shipped
        await PackingStartedAsync(t, orderId);
        var after = await OrderAsync(t, orderId);
        Assert.Equal((shipped.Status, shipped.Version, history), (after.Status, after.Version, (await HistoryAsync(t, orderId)).Count));

        await DeliveredAsync(t, orderId, at, refId: Guid.NewGuid()); // a reship's delivered moves the shipped order
        Assert.Equal(OrderStatuses.Delivered, (await OrderAsync(t, orderId)).Status);
        await DeliveredAsync(t, orderId, at.AddDays(1)); // a second delivered changes nothing
        var delivered = await OrderAsync(t, orderId);
        Assert.Equal(at, delivered.DeliveredAt);
        Assert.Single(await Outbox.PayloadsAsync<OrderDeliveredV1>(t, OrderMessages.Delivered));
    }

    [Fact]
    public async Task Delivery_failed_restores_the_coupon()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (_, orderId, _) = await PaidAsync(t, coupon: true);
        async Task<(string Status, int Uses)> StateAsync()
        {
            await using var db = t.NewDb();
            return (await db.CouponUses.AsNoTracking().Select(u => u.Status).SingleAsync(Ct), await db.Coupons.AsNoTracking().Select(c => c.UsesCount).SingleAsync(Ct));
        }
        Task FailedAsync() => Consume.Async<DeliveryFailedConsumer>(t, FulfillmentMessages.DeliveryFailed, new DeliveryFailedV1(orderId, "lost"));

        await FailedAsync(); // not shipped yet: no-op
        Assert.Equal(OrderStatuses.Paid, (await OrderAsync(t, orderId)).Status);
        Assert.Equal((CouponUseStatuses.Consumed, 1), await StateAsync());

        await PackingStartedAsync(t, orderId);
        await ShippedAsync(t, orderId);
        await FailedAsync();

        Assert.Equal(OrderStatuses.DeliveryFailed, (await OrderAsync(t, orderId)).Status);
        var last = (await HistoryAsync(t, orderId)).Last();
        Assert.Equal(("shipped", "delivery_failed", "system"), (last.FromStatus, last.ToStatus, last.ActorType));
        Assert.Equal((CouponUseStatuses.Restored, 0), await StateAsync());
        await FailedAsync(); // redelivery
        Assert.Equal((CouponUseStatuses.Restored, 0), await StateAsync());
    }

    [Fact]
    public async Task O19_all_units_returned_is_returned_and_partial_is_partially_returned()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (_, orderId, _) = await PaidAsync(t); // line 1: device x2, line 2: plan x1
        await SetStatusAsync(t, orderId, OrderStatuses.Delivered);
        var before = await OrderAsync(t, orderId);

        await DecidedAsync(t, orderId); // nothing accepted: unchanged (owner item 5)
        var same = await OrderAsync(t, orderId);
        Assert.Equal((before.Status, before.Version), (same.Status, same.Version));
        Assert.All(await LinesAsync(t, orderId), l => Assert.Equal(0, l.ReturnedQty));

        await DecidedAsync(t, orderId, (1, 1));
        Assert.Equal(OrderStatuses.PartiallyReturned, (await OrderAsync(t, orderId)).Status);
        Assert.Equal([1, 0], (await LinesAsync(t, orderId)).Select(l => l.ReturnedQty));

        await DecidedAsync(t, orderId, (1, 1), (2, 1));
        Assert.Equal(OrderStatuses.Returned, (await OrderAsync(t, orderId)).Status);
        Assert.Equal([2, 1], (await LinesAsync(t, orderId)).Select(l => l.ReturnedQty));
        var last = (await HistoryAsync(t, orderId)).TakeLast(2).Select(h => (h.FromStatus, h.ToStatus, h.ActorType)).ToList();
        Assert.Equal([("delivered", "partially_returned", "system"), ("partially_returned", "returned", "system")], last);
    }

    [Fact]
    public async Task Open_returns_count_up_and_down()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (_, orderId, _) = await PaidAsync(t);
        Task RequestedAsync() => Consume.Async<ReturnRequestedConsumer>(t, FulfillmentMessages.ReturnRequested, new ReturnRequestedV1(orderId, Guid.NewGuid()));
        Task ClosedAsync() => Consume.Async<ReturnClosedConsumer>(t, FulfillmentMessages.ReturnClosed, new ReturnClosedV1(orderId, Guid.NewGuid(), "decided"));

        await ClosedAsync(); // at 0 stays 0
        Assert.Equal(0, (await OrderAsync(t, orderId)).OpenReturns);
        await RequestedAsync();
        await RequestedAsync();
        Assert.Equal(2, (await OrderAsync(t, orderId)).OpenReturns);
        await ClosedAsync();
        Assert.Equal(1, (await OrderAsync(t, orderId)).OpenReturns);
        await ClosedAsync();
        await ClosedAsync();
        Assert.Equal(0, (await OrderAsync(t, orderId)).OpenReturns);
        await Consume.Async<ReturnRequestedConsumer>(t, FulfillmentMessages.ReturnRequested, new ReturnRequestedV1(Guid.NewGuid(), Guid.NewGuid())); // unknown order
    }

    [Fact]
    public async Task Refund_completed_updates_lines_and_completes_the_saga()
    {
        await using var t = await TestOrder.StartAsync(containers);

        // a fulfillment refund (return) has lines and no saga: only the line amounts change
        var (_, returned, _) = await PaidAsync(t);
        await RefundCompletedAsync(t, returned, (1, 500), (2, 100));
        await RefundCompletedAsync(t, returned, (1, 250));
        Assert.Equal([750L, 100L], (await LinesAsync(t, returned)).Select(l => l.RefundedCents));
        await using (var db = t.NewDb()) Assert.False(await db.Set<OrderSaga>().AnyAsync(s => s.OrderId == returned, Ct));

        // the order's own refund of a cancellation completes the step; the saga ends with its last step
        var orderId = await CancelledAsync(t);
        Assert.Equal([SagaSteps.InventoryRelease, SagaSteps.Refund], (await SagaAsync(t, orderId)).PendingSteps.Order());
        await Consume.Async<ReservationReleasedConsumer>(t, InventoryMessages.ReservationReleased, new ReservationReleasedV1(orderId));
        var open = await SagaAsync(t, orderId);
        Assert.Equal([SagaSteps.Refund], open.PendingSteps);
        Assert.Null(open.CompletedAt);

        await RefundCompletedAsync(t, orderId, (1, 700));
        var done = await SagaAsync(t, orderId);
        Assert.Empty(done.PendingSteps);
        Assert.Equal(t.Clock.GetUtcNow(), done.CompletedAt);
        Assert.Equal(700, (await LinesAsync(t, orderId))[0].RefundedCents);
    }

    [Fact]
    public async Task Refund_completed_without_lines_round_trips_and_completes_only_the_step()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var orderId = await CancelledAsync(t);
        await Consume.Async<ReservationReleasedConsumer>(t, InventoryMessages.ReservationReleased, new ReservationReleasedV1(orderId));

        // the message goes through the real writer and the typed envelope: Lines = null is written as an explicit null and read back
        await using (var scope = t.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<OutboxWriter>().PublishEvent(PaymentMessages.RefundCompleted, new RefundCompletedV1(orderId, Guid.NewGuid(), 4000, null));
            await scope.ServiceProvider.GetRequiredService<OrderDbContext>().SaveChangesAsync(Ct);
        }
        var envelope = Assert.Single(await Outbox.EnvelopesAsync(t, PaymentMessages.RefundCompleted));
        Assert.Equal(JsonValueKind.Null, envelope.Payload.GetProperty("lines").ValueKind);
        Assert.Null(envelope.GetPayload<RefundCompletedV1>().Lines);

        await Consume.Async<RefundCompletedConsumer>(t, PaymentMessages.RefundCompleted, envelope.Payload);

        var saga = await SagaAsync(t, orderId);
        Assert.Empty(saga.PendingSteps);
        Assert.NotNull(saga.CompletedAt);
        Assert.All(await LinesAsync(t, orderId), l => Assert.Equal(0, l.RefundedCents));
    }

    [Fact]
    public async Task Hold_released_completes_the_launch_release_step()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var orderId = await CancelledAsync(t);
        await using (var db = t.NewDb())
            await db.Database.ExecuteSqlAsync($"UPDATE order_sagas SET pending_steps = {new[] { SagaSteps.LaunchRelease, SagaSteps.Refund }} WHERE order_id = {orderId}", Ct);

        await Consume.Async<HoldReleasedConsumer>(t, LaunchMessages.HoldReleased, new HoldReleasedV1(orderId));

        Assert.Equal([SagaSteps.Refund], (await SagaAsync(t, orderId)).PendingSteps);
    }

    [Fact]
    public async Task Refund_failed_keeps_the_saga_open()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var orderId = await CancelledAsync(t);
        var refundId = Guid.NewGuid();

        await Consume.Async<RefundFailedConsumer>(t, PaymentMessages.RefundFailed, new RefundFailedV1(orderId, refundId, "card_closed"));

        var saga = await SagaAsync(t, orderId);
        Assert.Equal([SagaSteps.InventoryRelease, SagaSteps.RefundFailed], saga.PendingSteps.Order());
        Assert.Null(saga.CompletedAt);
        var log = Assert.Single(t.Logs.Entries, e => e.StartsWith("[Error]"));
        Assert.All(new[] { orderId.ToString(), refundId.ToString(), "card_closed" }, part => Assert.Contains(part, log));
        await Consume.Async<ReservationReleasedConsumer>(t, InventoryMessages.ReservationReleased, new ReservationReleasedV1(orderId));
        Assert.Null((await SagaAsync(t, orderId)).CompletedAt); // refund_failed still waits
    }

    [Fact]
    public async Task Line_refund_above_paid_is_rejected()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (_, orderId, _) = await PaidAsync(t);
        var paid = (await LinesAsync(t, orderId))[0].PaidCents;

        // the first line is valid, the second overflows (OR-05): the handler throws and the transaction writes nothing
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RefundCompletedAsync(t, orderId, (2, 100), (1, paid + 1)));
        Assert.Contains("ck_order_lines_refund_le_paid", error.ToString());

        Assert.All(await LinesAsync(t, orderId), l => Assert.Equal(0, l.RefundedCents));
    }
}
