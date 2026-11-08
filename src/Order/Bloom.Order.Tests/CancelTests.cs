using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Consumers;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Payment.Contracts;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using static Bloom.Order.Tests.Seed;

namespace Bloom.Order.Tests;

public class CancelTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    /// <summary>A paid regular order of one device (2 x 1000), with a 10% coupon when asked.</summary>
    private static async Task<(Guid Account, string OrderId)> PaidAsync(TestOrder t, bool coupon = false)
    {
        var account = Guid.NewGuid();
        if (coupon) await t.Rpc.Staff.CreateCouponAsync(new CreateCouponRequest { Code = "SAVE-10", Type = "percent", Value = 10, Scope = "order" }, TestCaller.Staff("catalog_manager").Call());
        await Seed.CartAsync(t, account, (Seed.Device(t.Upstreams, 1000), 2, null));
        var response = await Seed.CheckoutAsync(t, account, configure: r => { if (coupon) r.CouponCode = "SAVE-10"; });
        await SucceededAsync(t, Guid.Parse(response.OrderId));
        return (account, response.OrderId);
    }

    /// <summary>A paid launch order: awaiting stock, never handed to fulfillment.</summary>
    private static async Task<(Guid Account, string OrderId)> AwaitingStockAsync(TestOrder t)
    {
        var account = Guid.NewGuid();
        var response = await LaunchCheckoutAsync(t, account, Guid.NewGuid(), 2);
        await SucceededAsync(t, Guid.Parse(response.OrderId));
        return (account, response.OrderId);
    }

    private static async Task<string[]> SagaStepsAsync(TestOrder t, string orderId)
    {
        await using var db = t.NewDb();
        return [.. (await db.Set<OrderSaga>().AsNoTracking().SingleAsync(s => s.OrderId == Guid.Parse(orderId) && s.Type == SagaTypes.Cancellation, Ct)).PendingSteps.Order()];
    }

    /// <summary>Nothing changed: the row, its history, the sagas and the outbox are as before.</summary>
    private static async Task AssertUnchangedAsync(TestOrder t, string orderId, OrderRow before, int outbox, int history)
    {
        var order = await OrderAsync(t, orderId);
        Assert.Equal((before.Status, before.Version, before.CancelReason), (order.Status, order.Version, order.CancelReason));
        Assert.Equal(history, (await HistoryAsync(t, orderId)).Count);
        Assert.Equal(outbox, await OutboxCountAsync(t));
        await using var db = t.NewDb();
        Assert.False(await db.Set<OrderSaga>().AnyAsync(s => s.OrderId == Guid.Parse(orderId) && s.Type == SagaTypes.Cancellation, Ct));
    }

    [Fact]
    public async Task O12_customer_cancels_a_paid_order_before_packing()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (account, orderId) = await PaidAsync(t, coupon: true);
        var before = await OrderAsync(t, orderId);

        var cancelled = await t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId, Version = before.Version }, TestCaller.Customer(account).Call());

        Assert.Equal(("cancelled", "customer", before.Version + 1), (cancelled.Status, cancelled.CancelReason, cancelled.Version));
        var call = Assert.Single(t.Upstreams.Calls, c => c.Method == "CancelPackTask");
        Assert.Equal((orderId, "customer"), (((CancelPackTaskRequest)call.Request).OrderId, ((CancelPackTaskRequest)call.Request).By));
        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.Cancelled, CancelReasons.Customer), (order.Status, order.CancelReason));
        var last = (await HistoryAsync(t, orderId)).Last();
        Assert.Equal(("paid", "cancelled", "customer", account, (string?)null), (last.FromStatus, last.ToStatus, last.ActorType, last.ActorId, last.Reason));

        var refund = Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Equal((order.Id, order.TotalCents, "cancellation"), (refund.OrderId, refund.AmountCents, refund.Reason));
        Assert.Null(refund.Lines);
        Assert.Equal(order.Id, Assert.Single(await Outbox.PayloadsAsync<ReleaseReservationV1>(t, InventoryMessages.ReleaseReservation)).OrderId);
        Assert.Empty(await Outbox.PayloadsAsync<ReleaseAllocationHoldV1>(t, LaunchMessages.ReleaseAllocationHold));
        Assert.Equal([SagaSteps.InventoryRelease, SagaSteps.Refund], await SagaStepsAsync(t, orderId));
        var message = Assert.Single(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled));
        Assert.Equal((order.Id, order.Number, account, "customer"), (message.OrderId, message.OrderNumber, message.AccountId, message.Reason));
        Assert.DoesNotContain(await Outbox.AuditAsync(t), a => a.Action == "order.support-cancel-order"); // only Support's cancel is audited

        await using var db = t.NewDb();
        Assert.Equal(CouponUseStatuses.Restored, await db.CouponUses.AsNoTracking().Select(u => u.Status).SingleAsync(Ct));
        Assert.Equal(0, await db.Coupons.AsNoTracking().Select(c => c.UsesCount).SingleAsync(Ct));
        Assert.Equal(0, await db.CouponCustomerUsages.AsNoTracking().Select(u => u.UsesCount).SingleAsync(Ct));
        Assert.Equal(1, metrics.Sum("coupon_redemptions_total", ("result", "restored")));
        Assert.Equal(1, metrics.Sum("cancellations_total", ("actor", "customer"), ("result", "ok")));
    }

    [Fact]
    public async Task O13_customer_cancel_after_packing_started_is_CANCEL_NOT_ALLOWED()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (account, orderId) = await PaidAsync(t);
        var me = TestCaller.Customer(account);
        var before = await OrderAsync(t, orderId);
        var (outbox, history) = (await OutboxCountAsync(t), (await HistoryAsync(t, orderId)).Count);
        t.Upstreams.Fail("CancelPackTask", StatusCode.FailedPrecondition, "PACKING_STARTED");

        await Rpc.FailsAsync(t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId, Version = before.Version }, me.Call()), "CANCEL_NOT_ALLOWED");

        Assert.Equal(1, t.Upstreams.Count("CancelPackTask"));
        await AssertUnchangedAsync(t, orderId, before, outbox, history);
        Assert.Equal(1, metrics.Sum("cancellations_total", ("actor", "customer"), ("result", "not_allowed")));

        await SetStatusAsync(t, orderId, OrderStatuses.Packing); // packing: refused on the status, fulfillment is not asked
        before = await OrderAsync(t, orderId);
        await Rpc.FailsAsync(t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId, Version = before.Version }, me.Call()), "CANCEL_NOT_ALLOWED");
        Assert.Equal(1, t.Upstreams.Count("CancelPackTask"));
        await AssertUnchangedAsync(t, orderId, before, outbox, history);
        Assert.Equal(2, metrics.Sum("cancellations_total", ("actor", "customer"), ("result", "not_allowed")));
    }

    [Fact]
    public async Task O14_support_cancels_during_packing()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (account, orderId) = await PaidAsync(t);
        await SetStatusAsync(t, orderId, OrderStatuses.Packing);
        var before = await OrderAsync(t, orderId);
        var staff = TestCaller.Staff("support");

        var cancelled = await t.Rpc.Staff.SupportCancelOrderAsync(new SupportCancelOrderRequest { OrderId = orderId, Reason = "Fraud suspected", Version = before.Version }, staff.Call());

        Assert.Equal(("cancelled", "support"), (cancelled.Status, cancelled.CancelReason));
        var call = Assert.Single(t.Upstreams.Calls, c => c.Method == "CancelPackTask");
        Assert.Equal((orderId, "support"), (((CancelPackTaskRequest)call.Request).OrderId, ((CancelPackTaskRequest)call.Request).By));
        var last = (await HistoryAsync(t, orderId)).Last();
        Assert.Equal(("packing", "cancelled", "staff", staff.Id, "Fraud suspected"), (last.FromStatus, last.ToStatus, last.ActorType, last.ActorId, last.Reason));
        var audit = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal(("order", "order.support-cancel-order", "order", orderId, staff.Id, "support"), (audit.Service, audit.Action, audit.TargetType, audit.TargetId, audit.ActorId, audit.ActorRole));
        Assert.Equal([SagaSteps.InventoryRelease, SagaSteps.Refund], await SagaStepsAsync(t, orderId));
        Assert.Equal("support", Assert.Single(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled)).Reason);
        Assert.Equal(account, Assert.Single(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled)).AccountId);
        Assert.Equal(1, metrics.Sum("cancellations_total", ("actor", "support"), ("result", "ok")));

        // an order past cancelling is refused, and so is a bad reason
        var other = await PaidAsync(t);
        var current = await OrderAsync(t, other.OrderId);
        await SetStatusAsync(t, other.OrderId, OrderStatuses.Shipped);
        await Rpc.FailsAsync(t.Rpc.Staff.SupportCancelOrderAsync(new SupportCancelOrderRequest { OrderId = other.OrderId, Reason = "x", Version = current.Version }, staff.Call()), "CANCEL_NOT_ALLOWED");
        var bad = await Assert.ThrowsAsync<RpcException>(() => t.Rpc.Staff.SupportCancelOrderAsync(new SupportCancelOrderRequest { OrderId = other.OrderId, Reason = "", Version = current.Version }, staff.Call()).ResponseAsync);
        Assert.Equal("reason", Rpc.Info(bad).Metadata["field"]);
        await Rpc.FailsAsync(t.Rpc.Staff.SupportCancelOrderAsync(new SupportCancelOrderRequest { OrderId = other.OrderId, Reason = "x", Version = current.Version }, TestCaller.Staff("catalog_manager").Call()), "ROLE_NOT_ALLOWED");
    }

    [Fact]
    public async Task O15_support_cancel_already_shipped()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (_, orderId) = await PaidAsync(t);
        await SetStatusAsync(t, orderId, OrderStatuses.Packing);
        var before = await OrderAsync(t, orderId);
        var (outbox, history) = (await OutboxCountAsync(t), (await HistoryAsync(t, orderId)).Count);
        t.Upstreams.Fail("CancelPackTask", StatusCode.FailedPrecondition, "ALREADY_SHIPPED");

        await Rpc.FailsAsync(t.Rpc.Staff.SupportCancelOrderAsync(new SupportCancelOrderRequest { OrderId = orderId, Reason = "late", Version = before.Version }, TestCaller.Staff("admin").Call()), "ALREADY_SHIPPED");

        await AssertUnchangedAsync(t, orderId, before, outbox, history);
        Assert.Empty(await Outbox.AuditAsync(t)); // a refused cancel leaves no audit
        Assert.Equal(1, metrics.Sum("cancellations_total", ("actor", "support"), ("result", "already_shipped")));
    }

    [Fact]
    public async Task O16_launch_order_cancel_releases_both()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, orderId) = await AwaitingStockAsync(t);
        var before = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.AwaitingStock, false), (before.Status, before.HandedToFulfillment));

        var cancelled = await t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId, Version = before.Version }, TestCaller.Customer(account).Call());

        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal(0, t.Upstreams.Count("CancelPackTask")); // fulfillment never got the order
        var hold = Assert.Single(await Outbox.PayloadsAsync<ReleaseAllocationHoldV1>(t, LaunchMessages.ReleaseAllocationHold));
        Assert.Equal((before.Id, "customer"), (hold.OrderId, hold.Reason));
        Assert.Equal(before.Id, Assert.Single(await Outbox.PayloadsAsync<ReleaseReservationV1>(t, InventoryMessages.ReleaseReservation)).OrderId);
        var refund = Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment));
        Assert.Equal((before.TotalCents, "cancellation"), (refund.AmountCents, refund.Reason));
        Assert.Equal([SagaSteps.InventoryRelease, SagaSteps.LaunchRelease, SagaSteps.Refund], await SagaStepsAsync(t, orderId));
        Assert.Equal("customer", Assert.Single(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled)).Reason);
    }

    [Fact]
    public async Task Fulfillment_down_is_UNAVAILABLE_and_changes_nothing()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, orderId) = await PaidAsync(t);
        var before = await OrderAsync(t, orderId);
        var (outbox, history) = (await OutboxCountAsync(t), (await HistoryAsync(t, orderId)).Count);
        t.Upstreams.Fail("CancelPackTask", StatusCode.Unavailable);

        var e = await Assert.ThrowsAsync<RpcException>(() => t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId, Version = before.Version }, TestCaller.Customer(account).Call()).ResponseAsync);

        Assert.Equal(StatusCode.Unavailable, e.StatusCode);
        await AssertUnchangedAsync(t, orderId, before, outbox, history);

        // the fake recovers (it failed once): the same request now succeeds
        Assert.Equal("cancelled", (await t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId, Version = before.Version }, TestCaller.Customer(account).Call())).Status);
    }

    [Fact]
    public async Task Stale_version_is_CONCURRENCY_CONFLICT()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, orderId) = await PaidAsync(t);
        var before = await OrderAsync(t, orderId);
        var (outbox, history) = (await OutboxCountAsync(t), (await HistoryAsync(t, orderId)).Count);

        await Rpc.FailsAsync(t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId, Version = before.Version - 1 }, TestCaller.Customer(account).Call()), "CONCURRENCY_CONFLICT");

        Assert.Equal(0, t.Upstreams.Count("CancelPackTask"));
        await AssertUnchangedAsync(t, orderId, before, outbox, history);
    }
}
