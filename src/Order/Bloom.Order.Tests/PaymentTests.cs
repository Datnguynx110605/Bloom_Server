using Bloom.Catalog.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Consumers;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Payment.Contracts;
using Microsoft.EntityFrameworkCore;
using ApiCoupon = Bloom.Order.Api.Coupon;
using static Bloom.Order.Tests.Seed;

namespace Bloom.Order.Tests;

public class PaymentTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<(Guid Account, Guid OrderId, SkuPricing Device, SkuPricing Plan)> RegularOrderAsync(TestOrder t, string? couponCode = null)
    {
        var account = Guid.NewGuid();
        var device = Seed.Device(t.Upstreams, 1000);
        var plan = Seed.Plan(t.Upstreams, device, 9900);
        await Seed.CartAsync(t, account, (device, 2, null), (plan, 1, 1));
        var response = await Seed.CheckoutAsync(t, account, configure: r => { if (couponCode is not null) r.CouponCode = couponCode; });
        return (account, Guid.Parse(response.OrderId), device, plan);
    }

    private static async Task<Guid> LaunchOrderAsync(TestOrder t, Guid launchId)
    {
        return Guid.Parse((await LaunchCheckoutAsync(t, Guid.NewGuid(), launchId, 2)).OrderId);
    }

    private static async Task<List<(string? From, string To)>> HistoryAsync(TestOrder t, Guid orderId)
    {
        await using var db = t.NewDb();
        return (await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == orderId).OrderBy(h => h.Id).ToListAsync(Ct)).Select(h => (h.FromStatus, h.ToStatus)).ToList();
    }

    private static async Task<OrderSaga?> SagaAsync(TestOrder t, Guid orderId, string type)
    {
        await using var db = t.NewDb();
        return await db.Set<OrderSaga>().AsNoTracking().SingleOrDefaultAsync(s => s.OrderId == orderId && s.Type == type, Ct);
    }

    [Fact]
    public async Task Payment_of_a_regular_order_marks_it_paid_and_hands_over()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        await t.Rpc.Staff.CreateCouponAsync(new CreateCouponRequest { Code = "SAVE-10", Type = "percent", Value = 10, Scope = "order" }, TestCaller.Staff("catalog_manager").Call());
        var (account, orderId, device, plan) = await RegularOrderAsync(t, "SAVE-10");
        var before = await OrderAsync(t, orderId);

        await SucceededAsync(t, orderId);

        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.Paid, true, FakeUpstreams.PaymentIdOf(orderId.ToString("D"))), (order.Status, order.HandedToFulfillment, order.PaymentId));
        Assert.Null(order.DeadlineAt);
        Assert.Equal(before.Version + 1, order.Version);
        Assert.Equal<(string?, string)[]>([(null, "creating"), ("creating", "pending_payment"), ("pending_payment", "paid")], (await HistoryAsync(t, orderId)).ToArray());

        await using var db = t.NewDb();
        Assert.Equal(CouponUseStatuses.Consumed, await db.CouponUses.AsNoTracking().Select(u => u.Status).SingleAsync(Ct));
        Assert.Equal(1, metrics.Sum("coupon_redemptions_total", ("result", "consumed")));

        var paid = Assert.Single(await Outbox.PayloadsAsync<OrderPaidV1>(t, OrderMessages.Paid));
        Assert.Equal((orderId, order.Number, account, (Guid?)null, order.TotalCents), (paid.OrderId, paid.OrderNumber, paid.AccountId, paid.LaunchId, paid.TotalCents));
        Assert.Equal(t.Clock.GetUtcNow(), paid.PaidAt, TimeSpan.FromMilliseconds(1));
        var line = Assert.Single(paid.Lines); // no plan lines
        Assert.Equal((Guid.Parse(device.SkuId), 2), (line.SkuId, line.Quantity));

        var ready = Assert.Single(await Outbox.PayloadsAsync<OrderReadyToFulfillV1>(t, OrderMessages.ReadyToFulfill));
        Assert.Equal((orderId, account, order.Number, "standard", 0L, order.TotalCents), (ready.OrderId, ready.AccountId, ready.Number, ready.ShippingOption, ready.ShippingCents, ready.TotalCents));
        Assert.Equal((order.ShipTo.Name, order.ShipTo.City, order.ShipTo.State, order.ShipTo.Zip, order.ShipTo.Phone), (ready.ShipTo.Name, ready.ShipTo.City, ready.ShipTo.State, ready.ShipTo.Zip, ready.ShipTo.Phone));
        Assert.Equal(order.ShipTo.Lines, ready.ShipTo.Lines);
        Assert.Equal([(1, "product", Guid.Parse(device.SkuId), 2, (int?)null), (2, "warranty_plan", Guid.Parse(plan.SkuId), 1, 1)],
            ready.Lines.Select(l => (l.LineNo, l.Kind, l.SkuId, l.Quantity, l.ParentLineNo)));
        Assert.Equal(order.TotalCents, ready.Lines.Sum(l => l.PaidCents) + ready.ShippingCents);
    }

    [Fact]
    public async Task Payment_of_a_launch_order_waits_for_stock()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var launchId = Guid.NewGuid();
        var orderId = await LaunchOrderAsync(t, launchId);

        await SucceededAsync(t, orderId);

        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.AwaitingStock, false, false, false), (order.Status, order.HandedToFulfillment, order.LaunchReleased, order.StockAllocated));
        Assert.Null(order.DeadlineAt);
        Assert.Equal(("pending_payment", "awaiting_stock"), (await HistoryAsync(t, orderId)).Last());
        var paid = Assert.Single(await Outbox.PayloadsAsync<OrderPaidV1>(t, OrderMessages.Paid));
        Assert.Equal((launchId, order.TotalCents), (paid.LaunchId, paid.TotalCents));
        Assert.Equal(2, Assert.Single(paid.Lines).Quantity);
        Assert.Empty(await Outbox.PayloadsAsync<OrderReadyToFulfillV1>(t, OrderMessages.ReadyToFulfill));
    }

    [Fact]
    public async Task O09_payment_after_expiry_refunds_in_full()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (account, orderId, _, _) = await RegularOrderAsync(t);
        t.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await t.Sweeps().RunExpiryAsync(Ct));

        await SucceededAsync(t, orderId);

        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.Cancelled, CancelReasons.CheckoutExpired), (order.Status, order.CancelReason));
        var saga = await SagaAsync(t, orderId, SagaTypes.LatePayment);
        Assert.Equal([SagaSteps.Refund], saga!.PendingSteps);
        var refund = Assert.Single(await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment)); // read back the way payment reads it
        Assert.Equal((orderId, order.TotalCents, "late_payment"), (refund.OrderId, refund.AmountCents, refund.Reason));
        Assert.Null(refund.Lines);
        var late = Assert.Single(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled), c => c.Reason == "late_payment");
        Assert.Equal((orderId, order.Number, account), (late.OrderId, late.OrderNumber, late.AccountId));
        Assert.Equal(1, metrics.Sum("late_payments_total"));
        var rows = await OutboxCountAsync(t);

        await SucceededAsync(t, orderId, Guid.NewGuid()); // redelivered under a new message id

        Assert.Equal(rows, await OutboxCountAsync(t));
        Assert.Equal(1, metrics.Sum("late_payments_total"));
        Assert.Equal(CancelReasons.CheckoutExpired, (await OrderAsync(t, orderId)).CancelReason);
    }

    [Fact]
    public async Task Duplicate_success_is_a_no_op()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (_, orderId, _, _) = await RegularOrderAsync(t);
        await SucceededAsync(t, orderId);
        var (rows, order, history) = (await OutboxCountAsync(t), await OrderAsync(t, orderId), await HistoryAsync(t, orderId));

        await SucceededAsync(t, orderId, Guid.NewGuid());

        Assert.Equal(rows, await OutboxCountAsync(t));
        Assert.Equal(order.Version, (await OrderAsync(t, orderId)).Version);
        Assert.Equal(history, await HistoryAsync(t, orderId));
    }

    [Fact]
    public async Task Payment_failed_changes_nothing()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (_, orderId, _, _) = await RegularOrderAsync(t);
        var (rows, order, history) = (await OutboxCountAsync(t), await OrderAsync(t, orderId), await HistoryAsync(t, orderId));

        await Consume.Async<PaymentFailedConsumer>(t, PaymentMessages.Failed, new PaymentFailedV1(orderId, FakeUpstreams.PaymentIdOf(orderId.ToString("D")), "card_declined"));

        var after = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.PendingPayment, order.Version, order.DeadlineAt), (after.Status, after.Version, after.DeadlineAt));
        Assert.Equal(rows, await OutboxCountAsync(t));
        Assert.Equal(history, await HistoryAsync(t, orderId));
    }

    [Fact]
    public async Task Only_what_was_bought_leaves_the_cart()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, orderId, device, plan) = await RegularOrderAsync(t); // cart: device x2 (line 1), plan x1 (line 2)
        var other = Seed.Device(t.Upstreams, 500);
        await using (var db = t.NewDb()) // the customer keeps shopping while paying
        {
            await db.CartLines.Where(l => l.AccountId == account && l.LineNo == 1).ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, 3), Ct);
            db.CartLines.Add(new Bloom.Order.Data.CartLine { AccountId = account, LineNo = 3, Kind = "product", SkuId = Guid.Parse(other.SkuId), Quantity = 1 });
            await db.SaveChangesAsync(Ct);
        }

        await SucceededAsync(t, orderId);

        await using var after = t.NewDb();
        var lines = await after.CartLines.AsNoTracking().Where(l => l.AccountId == account).OrderBy(l => l.LineNo).ToListAsync(Ct);
        Assert.Equal([(Guid.Parse(device.SkuId), 1), (Guid.Parse(other.SkuId), 1)], lines.Select(l => (l.SkuId, l.Quantity)));
        Assert.DoesNotContain(lines, l => l.SkuId == Guid.Parse(plan.SkuId));
        Assert.Equal(2, await after.Carts.AsNoTracking().Where(c => c.AccountId == account).Select(c => c.Version).SingleAsync(Ct));
    }

    [Fact]
    public async Task A_plan_added_during_checkout_goes_with_its_device()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var account = Guid.NewGuid();
        var device = Seed.Device(t.Upstreams, 1000);
        var bought = Seed.Plan(t.Upstreams, device, 9900);
        var later = Seed.Plan(t.Upstreams, device, 5000, 24);
        await Seed.CartAsync(t, account, (device, 1, null), (bought, 1, 1));
        var orderId = Guid.Parse((await Seed.CheckoutAsync(t, account)).OrderId);
        await using (var db = t.NewDb())
        {
            db.CartLines.Add(new Bloom.Order.Data.CartLine { AccountId = account, LineNo = 3, Kind = "warranty_plan", SkuId = Guid.Parse(later.SkuId), ParentLineNo = 1, Quantity = 1 });
            await db.SaveChangesAsync(Ct);
        }

        await SucceededAsync(t, orderId);

        await using var after = t.NewDb();
        Assert.Empty(await after.CartLines.AsNoTracking().Where(l => l.AccountId == account).ToListAsync(Ct)); // the device left, so the plan on it did too
    }

    [Fact]
    public async Task Paid_vs_expired_race_has_one_winner()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var sweeps = t.Sweeps();
        for (var round = 0; round < 20; round++)
        {
            var (_, orderId, _, _) = await RegularOrderAsync(t);
            t.Clock.SetUtcNow((await OrderAsync(t, orderId)).DeadlineAt!.Value.AddMilliseconds(1)); // just past the deadline

            var head = TimeSpan.FromMilliseconds(round % 5 * 4); // the sweep starts 0, 4, … 16 ms after the consumer, so each side gets to be first
            await Task.WhenAll(Task.Run(() => SucceededAsync(t, orderId), Ct), Task.Run(async () => { await Task.Delay(head, Ct); return await sweeps.RunExpiryAsync(Ct); }, Ct));

            var order = await OrderAsync(t, orderId);
            var late = await SagaAsync(t, orderId, SagaTypes.LatePayment);
            var refunds = (await Outbox.PayloadsAsync<RefundPaymentV1>(t, PaymentMessages.RefundPayment)).Count(r => r.OrderId == orderId);
            if (order.Status == OrderStatuses.Paid)
            {
                Assert.Equal((null, 0), (late, refunds));
                Assert.Equal(("pending_payment", "paid"), (await HistoryAsync(t, orderId)).Last());
            }
            else
            {
                Assert.Equal((OrderStatuses.Cancelled, CancelReasons.CheckoutExpired), (order.Status, order.CancelReason));
                Assert.NotNull(late);
                Assert.Equal(1, refunds);
                Assert.Equal(("pending_payment", "cancelled"), (await HistoryAsync(t, orderId)).Last());
            }
        }
    }
}
