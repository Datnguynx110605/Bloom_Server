using Bloom.Catalog.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Consumers;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Payment.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Tests;

public class SweepTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly TimeSpan CreatingWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(15);

    private static async Task<Guid> PendingOrderAsync(TestOrder t, Action<StartCheckoutRequest>? configure = null)
    {
        var account = Guid.NewGuid();
        var device = Seed.Device(t.Upstreams, 1000);
        await Seed.CartAsync(t, account, (device, 2, null));
        return Guid.Parse((await Seed.CheckoutAsync(t, account, configure: configure)).OrderId);
    }

    /// <summary>An order a crash left in <c>creating</c> (its hold may exist; nothing else follows it).</summary>
    private static async Task<OrderRow> CreatingOrderAsync(TestOrder t, string kind = OrderKinds.Regular)
    {
        var now = t.Clock.GetUtcNow();
        var order = new OrderRow
        {
            Id = Guid.CreateVersion7(), Number = "BLM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), AccountId = Guid.NewGuid(), Kind = kind, LaunchId = kind == OrderKinds.Launch ? Guid.NewGuid() : null,
            Status = OrderStatuses.Creating, DeadlineAt = now + CreatingWindow, ShippingOption = "standard", ShipTo = new ShipToV1("n", ["l"], "c", "NV", "89501", "+17755550100"),
            Currency = "USD", CreatedAt = now, UpdatedAt = now,
        };
        await using var db = t.NewDb();
        db.Orders.Add(order);
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, ToStatus = OrderStatuses.Creating, ActorType = ActorTypes.Customer, ActorId = order.AccountId, At = now });
        await db.SaveChangesAsync(Ct);
        return order;
    }

    private static async Task<OrderRow> OrderAsync(TestOrder t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id, Ct);
    }

    private static async Task<OrderSaga> SagaAsync(TestOrder t, Guid orderId, string type)
    {
        await using var db = t.NewDb();
        return await db.Set<OrderSaga>().AsNoTracking().SingleAsync(s => s.OrderId == orderId && s.Type == type, Ct);
    }

    [Fact]
    public async Task O03_crash_after_hold_before_tx2_is_compensated_by_the_creating_sweep()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var order = await CreatingOrderAsync(t);
        t.Clock.Advance(CreatingWindow + TimeSpan.FromSeconds(1));

        Assert.Equal(1, await SweepRunner.Of(t).RunCreatingAsync(Ct));

        var swept = await OrderAsync(t, order.Id);
        Assert.Equal((OrderStatuses.Cancelled, CancelReasons.CheckoutFailed), (swept.Status, swept.CancelReason));
        await using (var db = t.NewDb())
        {
            var last = await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == order.Id).OrderBy(h => h.Id).LastAsync(Ct);
            Assert.Equal(("creating", "cancelled", ActorTypes.System, CancelReasons.CheckoutFailed), (last.FromStatus, last.ToStatus, last.ActorType, last.Reason));
        }
        Assert.Equal(order.Id, Assert.Single(await Outbox.PayloadsAsync<ReleaseReservationV1>(t, InventoryMessages.ReleaseReservation)).OrderId);
        var cancelled = Assert.Single(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled));
        Assert.Equal((order.Id, order.Number, order.AccountId, "checkout_failed"), (cancelled.OrderId, cancelled.OrderNumber, cancelled.AccountId, cancelled.Reason));
        var saga = await SagaAsync(t, order.Id, SagaTypes.CheckoutCompensation);
        Assert.Equal([SagaSteps.InventoryRelease], saga.PendingSteps);
        Assert.Null(saga.CompletedAt);

        await Consume.Async<ReservationReleasedConsumer>(t, InventoryMessages.ReservationReleased, new ReservationReleasedV1(order.Id));

        saga = await SagaAsync(t, order.Id, SagaTypes.CheckoutCompensation);
        Assert.Empty(saga.PendingSteps);
        Assert.Equal(t.Clock.GetUtcNow(), saga.CompletedAt!.Value, TimeSpan.FromMilliseconds(1));
        await Consume.Async<ReservationReleasedConsumer>(t, InventoryMessages.ReservationReleased, new ReservationReleasedV1(Guid.NewGuid())); // an unknown order is no error
    }

    [Fact]
    public async Task O08_expiry_after_15_minutes()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        await t.Rpc.Staff.CreateCouponAsync(new CreateCouponRequest { Code = "SAVE-10", Type = "percent", Value = 10, Scope = "order", TotalLimit = 5, PerCustomerLimit = 1 }, TestCaller.Staff("catalog_manager").Call());
        var orderId = await PendingOrderAsync(t, r => r.CouponCode = "SAVE-10");
        t.Clock.Advance(PaymentWindow + TimeSpan.FromSeconds(1));

        Assert.Equal(1, await SweepRunner.Of(t).RunExpiryAsync(Ct));

        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.Cancelled, CancelReasons.CheckoutExpired), (order.Status, order.CancelReason));
        await using var db = t.NewDb();
        var last = await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == orderId).OrderBy(h => h.Id).LastAsync(Ct);
        Assert.Equal(("pending_payment", "cancelled", ActorTypes.System, CancelReasons.CheckoutExpired), (last.FromStatus, last.ToStatus, last.ActorType, last.Reason));
        Assert.Equal(CouponUseStatuses.Released, await db.CouponUses.AsNoTracking().Select(u => u.Status).SingleAsync(Ct));
        Assert.Equal(0, await db.Coupons.AsNoTracking().Select(c => c.UsesCount).SingleAsync(Ct)); // both counters are back
        Assert.Equal(0, await db.CouponCustomerUsages.AsNoTracking().Select(u => u.UsesCount).SingleAsync(Ct));
        Assert.Equal(1, metrics.Sum("coupon_redemptions_total", ("result", "released")));
        Assert.Equal(orderId, Assert.Single(await Outbox.PayloadsAsync<ReleaseReservationV1>(t, InventoryMessages.ReleaseReservation)).OrderId);
        Assert.Equal(orderId, Assert.Single(await Outbox.PayloadsAsync<CancelPaymentV1>(t, PaymentMessages.CancelPayment)).OrderId);
        Assert.Equal("checkout_expired", Assert.Single(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled)).Reason);
        var saga = await SagaAsync(t, orderId, SagaTypes.Expiry);
        Assert.Equal([SagaSteps.InventoryRelease], saga.PendingSteps);
        Assert.Null(saga.CompletedAt);
        Assert.Equal(1, metrics.Sum("checkout_expired_total"));

        Assert.Equal(0, await SweepRunner.Of(t).RunExpiryAsync(Ct)); // nothing left to do
        Assert.Equal(1, metrics.Sum("checkout_expired_total"));
    }

    [Fact]
    public async Task Sweeps_leave_orders_before_their_deadline()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var creating = await CreatingOrderAsync(t);
        var pending = await PendingOrderAsync(t);
        t.Clock.Advance(CreatingWindow - TimeSpan.FromSeconds(1));
        var sweeps = SweepRunner.Of(t);

        Assert.Equal((0, 0), (await sweeps.RunCreatingAsync(Ct), await sweeps.RunExpiryAsync(Ct)));

        t.Clock.Advance(PaymentWindow - CreatingWindow); // the creating order is past its deadline now, the pending one is 1 s short
        Assert.Equal(0, await sweeps.RunExpiryAsync(Ct));
        Assert.Equal(OrderStatuses.PendingPayment, (await OrderAsync(t, pending)).Status);
        Assert.Equal(OrderStatuses.Creating, (await OrderAsync(t, creating.Id)).Status); // the expiry sweep does not touch creating orders
    }

    [Fact]
    public async Task Launch_order_expiry_releases_the_allocation_hold()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var launchId = Guid.NewGuid();
        var device = Seed.LaunchSku(t.Upstreams, launchId);
        var orderId = await PendingOrderAsync(t, r =>
            r.Launch = new LaunchSource { LaunchId = launchId.ToString("D"), PassToken = "pass-1", Lines = { new CheckoutLine { Kind = "product", SkuId = device.SkuId, Quantity = 1 } } });
        t.Clock.Advance(PaymentWindow + TimeSpan.FromSeconds(1));

        Assert.Equal(1, await SweepRunner.Of(t).RunExpiryAsync(Ct));

        var release = Assert.Single(await Outbox.PayloadsAsync<ReleaseAllocationHoldV1>(t, LaunchMessages.ReleaseAllocationHold));
        Assert.Equal((orderId, "checkout_expired"), (release.OrderId, release.Reason));
        Assert.Empty(await Outbox.PayloadsAsync<ReleaseReservationV1>(t, InventoryMessages.ReleaseReservation));
        Assert.Equal([SagaSteps.LaunchRelease], (await SagaAsync(t, orderId, SagaTypes.Expiry)).PendingSteps);
        Assert.Equal(orderId, Assert.Single(await Outbox.PayloadsAsync<CancelPaymentV1>(t, PaymentMessages.CancelPayment)).OrderId);
    }

    [Fact]
    public async Task Sweep_skips_an_order_locked_by_tx2()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var order = await CreatingOrderAsync(t);
        t.Clock.Advance(CreatingWindow + TimeSpan.FromSeconds(1));

        await using (var other = t.NewDb())
        {
            await using var tx = await other.Database.BeginTransactionAsync(Ct);
            await other.Database.ExecuteSqlAsync($"SELECT 1 FROM orders WHERE id = {order.Id} FOR UPDATE", Ct); // Tx 2 holds the row
            Assert.Equal(0, await SweepRunner.Of(t).RunCreatingAsync(Ct));
            Assert.Equal(OrderStatuses.Creating, (await OrderAsync(t, order.Id)).Status);
            await tx.CommitAsync(Ct);
        }

        Assert.Equal(1, await SweepRunner.Of(t).RunCreatingAsync(Ct));
        Assert.Equal(OrderStatuses.Cancelled, (await OrderAsync(t, order.Id)).Status);
    }
}
