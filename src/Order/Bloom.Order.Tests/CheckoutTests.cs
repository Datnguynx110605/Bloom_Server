using Bloom.Catalog.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Payment.Contracts;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ApiCoupon = Bloom.Order.Api.Coupon;

namespace Bloom.Order.Tests;

public class CheckoutTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<ApiCoupon> CouponAsync(TestOrder t, string code, Action<CreateCouponRequest>? edit = null)
    {
        var request = new CreateCouponRequest { Code = code, Type = "percent", Value = 10, Scope = "order" };
        edit?.Invoke(request);
        return await t.Rpc.Staff.CreateCouponAsync(request, TestCaller.Staff("catalog_manager").Call());
    }

    /// <summary>A customer whose cart is one device line.</summary>
    private static async Task<(Guid Account, SkuPricing Device)> CartOfOneAsync(TestOrder t, int quantity = 1, long priceCents = 1000)
    {
        var account = Guid.NewGuid();
        var device = Seed.Device(t.Upstreams, priceCents);
        await Seed.CartAsync(t, account, (device, quantity, null));
        return (account, device);
    }

    private sealed record Outcome(Guid Account, StartCheckoutResponse? Response, RpcException? Error);

    private static async Task<RpcException> FailsAsync(AsyncUnaryCall<StartCheckoutResponse> call, StatusCode code, string reason)
    {
        var e = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Rpc.Info(e).Reason);
        return e;
    }

    private static async Task UnavailableAsync(AsyncUnaryCall<StartCheckoutResponse> call) =>
        Assert.Equal(StatusCode.Unavailable, (await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync)).StatusCode);

    private static async Task<OrderRow> OrderOfAsync(TestOrder t, Guid account)
    {
        await using var db = t.NewDb();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.AccountId == account, Ct);
    }

    private static async Task<List<(string? From, string To)>> HistoryAsync(TestOrder t, Guid orderId)
    {
        await using var db = t.NewDb();
        return (await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == orderId).OrderBy(h => h.Id).ToListAsync(Ct)).Select(h => (h.FromStatus, h.ToStatus)).ToList();
    }

    /// <summary>The order is cancelled as <c>checkout_failed</c>, with its history row and its <c>order.cancelled.v1</c>.</summary>
    private static async Task<OrderRow> AssertCancelledAsync(TestOrder t, Guid account)
    {
        var order = await OrderOfAsync(t, account);
        Assert.Equal((OrderStatuses.Cancelled, CancelReasons.CheckoutFailed), (order.Status, order.CancelReason));
        Assert.Equal((OrderStatuses.Creating, OrderStatuses.Cancelled), (await HistoryAsync(t, order.Id)).Last());
        var cancelled = (await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled)).Single(c => c.OrderId == order.Id);
        Assert.Equal((order.Number, account, "checkout_failed"), (cancelled.OrderNumber, cancelled.AccountId, cancelled.Reason));
        return order;
    }

    private static async Task<IReadOnlyList<ReleaseReservationV1>> ReleasesAsync(TestOrder t) =>
        await Outbox.PayloadsAsync<ReleaseReservationV1>(t, InventoryMessages.ReleaseReservation);

    [Fact]
    public async Task O01_regular_checkout_happy_path()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = Guid.NewGuid();
        var device = Seed.Device(t.Upstreams, 1000);
        var plan = Seed.Plan(t.Upstreams, device, 9900);
        await Seed.CartAsync(t, account, (device, 2, null), (plan, 1, 1));
        await CouponAsync(t, "SAVE-10", r => r.TotalLimit = 5);

        var response = await Seed.CheckoutAsync(t, account, configure: r => r.CouponCode = "save-10");

        // device 2 x 1000 = 2000, 10 % off = 200, tax on 1800 = 149 (half-cent up); the plan is untaxed: 1949 + 9900
        Assert.Equal((11900L, 200L, 149L, 0L, 11849L), (response.SubtotalCents, response.DiscountCents, response.TaxCents, response.ShippingCents, response.TotalCents));
        Assert.Equal($"https://pay.test/{response.OrderId}", response.PaymentUrl);
        Assert.Equal(t.Clock.GetUtcNow().AddMinutes(15), response.DeadlineAt.ToDateTimeOffset(), TimeSpan.FromMilliseconds(1));

        await using var db = t.NewDb();
        var order = await db.Orders.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((response.OrderId, response.Number, OrderStatuses.PendingPayment, OrderKinds.Regular, 825), (order.Id.ToString("D"), order.Number, order.Status, order.Kind, order.TaxRateBp));
        Assert.Equal(response.TotalCents, order.TotalCents);
        Assert.Equal(FakeUpstreams.PaymentIdOf(response.OrderId), order.PaymentId);
        Assert.Equal(t.Clock.GetUtcNow().AddMinutes(15), order.DeadlineAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal<(string?, string)[]>([(null, "creating"), ("creating", "pending_payment")], (await HistoryAsync(t, order.Id)).ToArray());

        var lines = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == order.Id).OrderBy(l => l.LineNo).ToListAsync(Ct);
        Assert.Equal([(1, "product", 200L, 149L, 1949L, (int?)null), (2, "warranty_plan", 0L, 0L, 9900L, 1)],
            lines.Select(l => (l.LineNo, l.Kind, l.DiscountCents, l.TaxCents, l.PaidCents, l.ParentLineNo)));
        Assert.Equal(order.TotalCents, lines.Sum(l => l.PaidCents) + order.ShippingCents);

        var reserve = Assert.Single(t.Upstreams.Calls, c => c.Method == "ReserveStock");
        var held = (ReserveStockRequest)reserve.Request;
        Assert.Equal(response.OrderId, held.OrderId);
        var line = Assert.Single(held.Lines); // product lines only
        Assert.Equal((device.SkuId, 2), (line.SkuId, line.Quantity));

        var coupon = await db.Coupons.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(1, coupon.UsesCount);
        var use = await db.CouponUses.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((CouponUseStatuses.Held, coupon.Id, account), (use.Status, use.CouponId, use.AccountId));
        Assert.Equal(1, await db.CouponCustomerUsages.AsNoTracking().Select(u => u.UsesCount).SingleAsync(Ct));

        var payment = (CreatePaymentRequest)Assert.Single(t.Upstreams.Calls, c => c.Method == "CreatePayment").Request;
        Assert.Equal((response.OrderId, response.Number, account.ToString("D"), 11849L), (payment.OrderId, payment.OrderNumber, payment.AccountId, payment.AmountCents));
        Assert.Equal([("Phone 1000 × 2", 1949L), ("Plan 12 months × 1", 9900L)], payment.DisplayLines.Select(l => (l.Name, l.AmountCents)));
        Assert.Equal(payment.AmountCents, payment.DisplayLines.Sum(l => l.AmountCents));

        Assert.Equal(1, metrics.Sum("checkout_total", ("kind", "regular"), ("result", "ok")));
        Assert.Equal(1, metrics.Count("checkout_start_seconds", ("kind", "regular")));
        Assert.Equal(1, metrics.Sum("coupon_redemptions_total", ("result", "held")));
        Assert.Empty(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled));
    }

    [Fact]
    public async Task OR01_checkout_start_seconds_leaves_out_the_payment_call()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (account, _) = await CartOfOneAsync(t);
        t.Upstreams.Before = method => method == "CreatePayment" ? Task.Delay(TimeSpan.FromSeconds(1), Ct) : Task.CompletedTask;

        var wall = System.Diagnostics.Stopwatch.StartNew();
        var response = await Seed.CheckoutAsync(t, account);
        wall.Stop();

        Assert.Equal(OrderStatuses.PendingPayment, (await OrderOfAsync(t, account)).Status);
        Assert.NotEmpty(response.PaymentUrl);
        Assert.True(wall.Elapsed >= TimeSpan.FromSeconds(1), $"the call took {wall.Elapsed}");
        Assert.True(Assert.Single(metrics.Values("checkout_start_seconds", ("kind", "regular"))) < 1.0);
    }

    [Fact]
    public async Task O02_retry_with_the_same_key_after_success_replays()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();

        var first = await Seed.CheckoutAsync(t, account, key);
        var again = await Seed.CheckoutAsync(t, account, key);

        Assert.Equal(first, again); // same order id, number, totals, url and deadline
        Assert.Equal(1, t.Upstreams.Count("ReserveStock"));
        Assert.Equal(1, t.Upstreams.Count("CreatePayment"));
        await using var db = t.NewDb();
        Assert.Equal(1, await db.Orders.CountAsync(Ct));
    }

    [Fact]
    public async Task O04_sold_out_cancels_and_is_replayed()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.FailedPrecondition, "SOLD_OUT");

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "SOLD_OUT");

        var order = await AssertCancelledAsync(t, account);
        await using var db = t.NewDb();
        Assert.Empty(await db.OrderSagas.ToListAsync(Ct)); // a single-service hold has nothing to release
        Assert.Empty(await ReleasesAsync(t));
        Assert.Equal(1, metrics.Sum("checkout_total", ("kind", "regular"), ("result", "sold_out")));

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "SOLD_OUT");
        Assert.Equal(1, t.Upstreams.Count("ReserveStock")); // the stored answer, no new call
        Assert.Equal(order.Id, (await OrderOfAsync(t, account)).Id);
        Assert.Equal(1, metrics.Sum("checkout_total", ("kind", "regular"), ("result", "sold_out"))); // a replay is not a new outcome
    }

    [Fact]
    public async Task CHK_R20_full_coupon_with_standard_shipping_is_refused()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (account, _) = await CartOfOneAsync(t);
        await CouponAsync(t, "FREE-100", r => r.Value = 100);
        var key = Guid.NewGuid();

        await FailsAsync(Seed.CheckoutAsync(t, account, key, r => r.CouponCode = "FREE-100"), StatusCode.FailedPrecondition, "ORDER_TOTAL_TOO_LOW");

        await AssertCancelledAsync(t, account);
        Assert.Equal(0, t.Upstreams.Count("ReserveStock"));
        Assert.Equal(1, metrics.Sum("checkout_total", ("kind", "regular"), ("result", "order_total_too_low")));
        await FailsAsync(Seed.CheckoutAsync(t, account, key, r => r.CouponCode = "FREE-100"), StatusCode.FailedPrecondition, "ORDER_TOTAL_TOO_LOW"); // stored on the key
        Assert.Equal(0, t.Upstreams.Count("ReserveStock"));
    }

    [Fact]
    public async Task CHK_R20_total_of_exactly_50_succeeds()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t, priceCents: 46); // 46 + 4 tax

        var response = await Seed.CheckoutAsync(t, account);

        Assert.Equal(50, response.TotalCents);
    }

    [Fact]
    public async Task CHK_R20_total_of_49_is_refused()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t, priceCents: 45); // 45 + 4 tax

        await FailsAsync(Seed.CheckoutAsync(t, account), StatusCode.FailedPrecondition, "ORDER_TOTAL_TOO_LOW");

        await AssertCancelledAsync(t, account);
    }

    [Fact]
    public async Task O05_last_coupon_use_two_concurrent_checkouts()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (first, _) = await CartOfOneAsync(t);
        var (second, _) = await CartOfOneAsync(t);
        var coupon = await CouponAsync(t, "LAST-ONE", r => r.TotalLimit = 1);
        // Both checkouts pass the pre-check and the hold before either reaches Tx 2, so the guarded counter decides.
        var arrived = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.Upstreams.Before = async method =>
        {
            if (method != "ReserveStock") return;
            if (Interlocked.Increment(ref arrived) == 2) gate.SetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        };

        var outcomes = await Task.WhenAll(new[] { first, second }.Select(async account =>
        {
            try { return new Outcome(account, await Seed.CheckoutAsync(t, account, configure: r => r.CouponCode = "LAST-ONE"), null); }
            catch (RpcException e) { return new Outcome(account, null, e); }
        }));

        var winner = Assert.Single(outcomes, o => o.Response is not null);
        var loser = Assert.Single(outcomes, o => o.Error is not null);
        Assert.Equal("COUPON_LIMIT_REACHED", Rpc.Info(loser.Error!).Reason);
        Assert.Equal(OrderStatuses.PendingPayment, (await OrderOfAsync(t, winner.Account)).Status);

        var lost = await AssertCancelledAsync(t, loser.Account);
        var release = Assert.Single(await ReleasesAsync(t)); // the hold of the loser is released
        Assert.Equal(lost.Id, release.OrderId);
        await using var db = t.NewDb();
        var saga = await db.OrderSagas.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((lost.Id, SagaTypes.CheckoutCompensation, null), (saga.OrderId, saga.Type, saga.CompletedAt));
        Assert.Equal([SagaSteps.InventoryRelease], saga.PendingSteps);
        Assert.Equal(1, (await db.Coupons.AsNoTracking().SingleAsync(c => c.Id == Guid.Parse(coupon.Id), Ct)).UsesCount);
        Assert.Equal(winner.Account, await db.CouponUses.AsNoTracking().Select(u => u.AccountId).SingleAsync(Ct));
    }

    [Fact]
    public async Task Retry_after_payment_unavailable_resumes_the_same_order()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        await CouponAsync(t, "SAVE-10");
        var key = Guid.NewGuid();
        t.Upstreams.Fail("CreatePayment", StatusCode.Unavailable);

        await UnavailableAsync(Seed.CheckoutAsync(t, account, key, r => r.CouponCode = "SAVE-10"));

        var order = await OrderOfAsync(t, account);
        Assert.Equal((OrderStatuses.PendingPayment, null), (order.Status, order.PaymentId));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains(order.Id.ToString("D")));

        var response = await Seed.CheckoutAsync(t, account, key, r => r.CouponCode = "SAVE-10");

        Assert.Equal((order.Id.ToString("D"), order.Number, $"https://pay.test/{order.Id}"), (response.OrderId, response.Number, response.PaymentUrl));
        Assert.Equal(2, t.Upstreams.Count("CreatePayment"));
        Assert.Equal(1, t.Upstreams.Count("ReserveStock")); // the hold is already placed: the order resumes from pending_payment
        await using var db = t.NewDb();
        Assert.Equal(1, await db.Orders.CountAsync(Ct));
        Assert.Equal(1, await db.CouponUses.CountAsync(Ct));
        Assert.Equal(1, (await db.Coupons.AsNoTracking().SingleAsync(Ct)).UsesCount);
        Assert.Equal(FakeUpstreams.PaymentIdOf(response.OrderId), (await OrderOfAsync(t, account)).PaymentId);
    }

    [Fact]
    public async Task Retry_after_hold_timeout_resumes_from_creating()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.Unavailable);

        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));

        var order = await OrderOfAsync(t, account);
        Assert.Equal(OrderStatuses.Creating, order.Status);

        var response = await Seed.CheckoutAsync(t, account, key);

        Assert.Equal(order.Id.ToString("D"), response.OrderId);
        Assert.Equal(OrderStatuses.PendingPayment, (await OrderOfAsync(t, account)).Status);
        var holds = t.Upstreams.Calls.Where(c => c.Method == "ReserveStock").Select(c => (ReserveStockRequest)c.Request).ToList();
        Assert.Equal([response.OrderId, response.OrderId], holds.Select(h => h.OrderId));
        Assert.Equal(holds[0].Lines, holds[1].Lines);
        await using var db = t.NewDb();
        Assert.Equal(1, await db.Orders.CountAsync(Ct));
    }

    [Fact]
    public async Task Retry_with_the_same_key_and_another_shipping_option_is_IDEMPOTENCY_KEY_REUSED()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.Unavailable);
        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));
        var calls = t.Upstreams.Calls.Count;

        await FailsAsync(Seed.CheckoutAsync(t, account, key, r => r.ShippingOption = "express"), StatusCode.InvalidArgument, "IDEMPOTENCY_KEY_REUSED");

        Assert.Equal(calls, t.Upstreams.Calls.Count);
        var order = await OrderOfAsync(t, account);
        Assert.Equal((OrderStatuses.Creating, "standard"), (order.Status, order.ShippingOption));
    }

    [Fact]
    public async Task Retry_with_the_same_key_and_another_address_is_IDEMPOTENCY_KEY_REUSED()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.Unavailable);
        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));

        await FailsAsync(Seed.CheckoutAsync(t, account, key, r => r.ShipTo.Zip = "89502"), StatusCode.InvalidArgument, "IDEMPOTENCY_KEY_REUSED");

        Assert.Equal(OrderStatuses.Creating, (await OrderOfAsync(t, account)).Status);
    }

    [Fact]
    public async Task Resumed_order_hold_conflict_also_releases()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.Unavailable); // inventory did hold: the call is recorded, then it answers badly
        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));
        t.Upstreams.Fail("ReserveStock", StatusCode.FailedPrecondition, "RESERVATION_CONFLICT");

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "RESERVATION_CONFLICT");

        var order = await AssertCancelledAsync(t, account);
        Assert.Equal(order.Id, Assert.Single(await ReleasesAsync(t)).OrderId);
        await using var db = t.NewDb();
        var saga = await db.OrderSagas.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((order.Id, SagaTypes.CheckoutCompensation), (saga.OrderId, saga.Type));
        Assert.Equal([SagaSteps.InventoryRelease], saga.PendingSteps);

        await using (var scope = t.Services.CreateAsyncScope()) // the release is confirmed: the step goes and the saga completes
        {
            var scoped = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await using var tx = await scoped.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<Sagas>().CompleteStepAsync(order.Id, SagaSteps.InventoryRelease, Ct);
            await tx.CommitAsync(Ct);
        }
        await using var verify = t.NewDb();
        var done = await verify.OrderSagas.AsNoTracking().SingleAsync(Ct);
        Assert.Empty(done.PendingSteps);
        Assert.Equal(t.Clock.GetUtcNow(), done.CompletedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Resumed_order_rejected_before_the_hold_also_releases()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, device) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.Unavailable); // inventory did hold: the call is recorded, then it answers badly
        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));
        var withdrawn = device.Clone();
        withdrawn.Buyable = false;
        t.Upstreams.Sku(withdrawn);

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "SKU_NOT_BUYABLE");

        var order = await AssertCancelledAsync(t, account);
        Assert.Equal(order.Id, Assert.Single(await ReleasesAsync(t)).OrderId);
        await using var db = t.NewDb();
        var saga = await db.OrderSagas.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((order.Id, SagaTypes.CheckoutCompensation), (saga.OrderId, saga.Type));
        Assert.Equal([SagaSteps.InventoryRelease], saga.PendingSteps);

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "SKU_NOT_BUYABLE"); // stored on the key
        Assert.Equal(1, t.Upstreams.Count("ReserveStock"));
        Assert.Single(await ReleasesAsync(t));
    }

    [Fact]
    public async Task Swept_order_on_resume_is_CHECKOUT_CLOSED()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.Unavailable);
        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));
        await using (var db = t.NewDb()) // the creating sweep got there first
            await db.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatuses.Cancelled).SetProperty(o => o.CancelReason, CancelReasons.CheckoutExpired), Ct);

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CHECKOUT_CLOSED");
        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CHECKOUT_CLOSED"); // stored on the key

        Assert.Equal(1, t.Upstreams.Count("ReserveStock"));
        var order = await OrderOfAsync(t, account);
        Assert.Equal((OrderStatuses.Cancelled, CancelReasons.CheckoutExpired), (order.Status, order.CancelReason)); // untouched
        Assert.Empty(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled));

        // swept between the hold and Tx 2: Tx 2 finds the order no longer creating (owner item 1)
        var (late, _) = await CartOfOneAsync(t);
        t.Upstreams.Before = async method =>
        {
            if (method != "ReserveStock") return;
            await using var sweep = t.NewDb();
            await sweep.Orders.Where(o => o.AccountId == late).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatuses.Cancelled).SetProperty(o => o.CancelReason, CancelReasons.CheckoutExpired), Ct);
        };

        await FailsAsync(Seed.CheckoutAsync(t, late), StatusCode.FailedPrecondition, "CHECKOUT_CLOSED");

        Assert.Equal(CancelReasons.CheckoutExpired, (await OrderOfAsync(t, late)).CancelReason);
        await using var db2 = t.NewDb();
        Assert.Empty(await db2.OrderLines.Where(l => l.OrderId == db2.Orders.Single(o => o.AccountId == late).Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task D17_sweep_release_then_late_reserve_is_CHECKOUT_CLOSED()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.FailedPrecondition, "ORDER_RELEASED"); // inventory tombstoned the order when the sweep's release arrived: it holds nothing
        t.Upstreams.Before = async method =>
        {
            if (method != "ReserveStock") return;
            t.Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
            await t.Sweeps().RunCreatingAsync(Ct); // the creating sweep cancels the order and sends the release while the reserve is on its way
        };

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CHECKOUT_CLOSED");
        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CHECKOUT_CLOSED"); // stored on the key

        var order = await OrderOfAsync(t, account);
        Assert.Equal((OrderStatuses.Cancelled, CancelReasons.CheckoutFailed), (order.Status, order.CancelReason));
        Assert.Equal(1, t.Upstreams.Count("ReserveStock"));
        Assert.Equal(order.Id, Assert.Single(await ReleasesAsync(t)).OrderId); // the sweep's release only: nothing is left reserved and nothing more is sent
        Assert.Single(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled));
        await using var db = t.NewDb();
        Assert.Equal(SagaTypes.CheckoutCompensation, (await db.OrderSagas.AsNoTracking().SingleAsync(Ct)).Type);
        Assert.Empty(await db.OrderLines.Where(l => l.OrderId == order.Id).ToListAsync(Ct));
    }

    /// <summary>An order whose <c>CreatePayment</c> failed once: <c>pending_payment</c>, key released, ready for a retry with the same key.</summary>
    private static async Task<(Guid Account, Guid Key, OrderRow Order)> StuckAtPaymentAsync(TestOrder t)
    {
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("CreatePayment", StatusCode.Unavailable);
        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));
        var order = await OrderOfAsync(t, account);
        Assert.Equal(OrderStatuses.PendingPayment, order.Status);
        return (account, key, order);
    }

    private static async Task SqlAsync(TestOrder t, FormattableString sql)
    {
        await using var db = t.NewDb();
        await db.Database.ExecuteSqlAsync(sql, Ct);
    }

    [Fact]
    public async Task D21_creating_resume_continues_at_the_hold()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("ReserveStock", StatusCode.Unavailable);
        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));
        var order = await OrderOfAsync(t, account);
        Assert.Equal(OrderStatuses.Creating, order.Status);
        await SqlAsync(t, $"UPDATE orders SET deadline_at = NULL WHERE id = {order.Id}"); // a creating order without a deadline must not crash the resume

        var response = await Seed.CheckoutAsync(t, account, key);

        Assert.Equal((order.Id.ToString("D"), $"https://pay.test/{order.Id}"), (response.OrderId, response.PaymentUrl));
        Assert.NotNull(response.DeadlineAt);
        Assert.Equal(2, t.Upstreams.Count("ReserveStock"));
        Assert.Equal(1, t.Upstreams.Count("CreatePayment"));
        Assert.Equal(OrderStatuses.PendingPayment, (await OrderOfAsync(t, account)).Status);
    }

    [Fact]
    public async Task D21_pending_payment_before_the_deadline_continues_at_payment()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, key, order) = await StuckAtPaymentAsync(t);

        var response = await Seed.CheckoutAsync(t, account, key);

        Assert.Equal((order.Id.ToString("D"), $"https://pay.test/{order.Id}"), (response.OrderId, response.PaymentUrl));
        Assert.Equal(2, t.Upstreams.Count("CreatePayment"));
        Assert.Equal(1, t.Upstreams.Count("ReserveStock"));
    }

    [Fact]
    public async Task D21_pending_payment_at_the_deadline_is_CHECKOUT_CLOSED()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, key, order) = await StuckAtPaymentAsync(t);
        await SqlAsync(t, $"UPDATE orders SET deadline_at = now() WHERE id = {order.Id}"); // R9: deadline_at <= now() has passed

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CHECKOUT_CLOSED");
        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CHECKOUT_CLOSED"); // stored on the key

        Assert.Equal(1, t.Upstreams.Count("CreatePayment")); // no payment session for an order the sweep is about to expire
        Assert.Equal(OrderStatuses.PendingPayment, (await OrderOfAsync(t, account)).Status); // the expiry sweep owns the cancellation
    }

    [Theory]
    [InlineData(OrderStatuses.Paid)]
    [InlineData(OrderStatuses.AwaitingStock)]
    public async Task D21_paid_or_awaiting_stock_resume_has_an_empty_payment_url(string status)
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, key, order) = await StuckAtPaymentAsync(t);
        await SqlAsync(t, $"UPDATE orders SET status = {status}, deadline_at = NULL WHERE id = {order.Id}"); // payment confirmed meanwhile: payment clears the deadline

        var response = await Seed.CheckoutAsync(t, account, key);

        Assert.Equal((order.Id.ToString("D"), order.Number, order.TotalCents, ""), (response.OrderId, response.Number, response.TotalCents, response.PaymentUrl));
        Assert.Null(response.DeadlineAt);
        Assert.Equal(1, t.Upstreams.Count("CreatePayment"));
        Assert.Equal(status, (await OrderOfAsync(t, account)).Status);
    }

    [Fact]
    public async Task D21_cancelled_resume_is_CHECKOUT_CLOSED()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, key, order) = await StuckAtPaymentAsync(t);
        await SqlAsync(t, $"UPDATE orders SET status = 'cancelled', cancel_reason = 'checkout_expired' WHERE id = {order.Id}");

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CHECKOUT_CLOSED");

        Assert.Equal(1, t.Upstreams.Count("CreatePayment"));
    }

    [Fact]
    public async Task Empty_cart_is_CART_EMPTY()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var account = Guid.NewGuid();
        var key = Guid.NewGuid();

        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CART_EMPTY");
        await FailsAsync(Seed.CheckoutAsync(t, account, key), StatusCode.FailedPrecondition, "CART_EMPTY");

        await AssertCancelledAsync(t, account);
        Assert.Empty(t.Upstreams.Calls);
    }

    [Fact]
    public async Task Validation_failures_cancel_and_are_stored()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var launchId = Guid.NewGuid();
        var withPlan = Seed.Device(t.Upstreams);
        var cases = new (StatusCode Code, string Reason, Func<Guid, Task> Seeds)[]
        {
            (StatusCode.FailedPrecondition, "SKU_NOT_BUYABLE", async a =>
            {
                var gone = Seed.Device(t.Upstreams);
                await Seed.CartAsync(t, a, (gone, 1, null));
                var withdrawn = gone.Clone();
                withdrawn.Buyable = false; // withdrawn after it went into the cart
                t.Upstreams.Sku(withdrawn);
            }),
            (StatusCode.FailedPrecondition, "SKU_NOT_BUYABLE", a => Seed.CartAsync(t, a, (Seed.LaunchSku(t.Upstreams, launchId), 1, null))), // a launch SKU before its release
            (StatusCode.InvalidArgument, "QUANTITY_LIMIT", a => Seed.CartAsync(t, a, (Seed.Device(t.Upstreams), 11, null))),
            (StatusCode.InvalidArgument, "WARRANTY_NOT_ELIGIBLE", a => Seed.CartAsync(t, a, (withPlan, 1, null), (Seed.Plan(t.Upstreams, withPlan), 2, 1))),
        };

        foreach (var (code, reason, seeds) in cases)
        {
            var account = Guid.NewGuid();
            var key = Guid.NewGuid();
            await seeds(account);

            await FailsAsync(Seed.CheckoutAsync(t, account, key), code, reason);
            await FailsAsync(Seed.CheckoutAsync(t, account, key), code, reason); // stored on the key

            await AssertCancelledAsync(t, account);
        }
        Assert.Equal(0, t.Upstreams.Count("ReserveStock")); // nothing was held
    }

    [Fact]
    public async Task Coupon_reasons()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var now = t.Clock.GetUtcNow();
        var cases = new (string Reason, string Code, Action<CreateCouponRequest>? Edit, bool Deactivate)[]
        {
            ("COUPON_NOT_FOUND", "NO-SUCH", null, false),
            ("COUPON_INACTIVE", "OFF-1", null, true),
            ("COUPON_NOT_STARTED", "SOON-1", r => r.StartsAt = Timestamp.FromDateTimeOffset(now.AddDays(1)), false),
            ("COUPON_EXPIRED", "OLD-1", r => r.ExpiresAt = Timestamp.FromDateTimeOffset(now.AddHours(-1)), false),
            ("COUPON_MIN_SPEND", "BIG-1", r => r.MinSpendCents = 1_000_000, false),
        };
        foreach (var (reason, code, edit, deactivate) in cases)
        {
            var (account, _) = await CartOfOneAsync(t);
            if (reason != "COUPON_NOT_FOUND")
            {
                var coupon = await CouponAsync(t, code, edit);
                if (deactivate)
                    await t.Rpc.Staff.DeactivateCouponAsync(new DeactivateCouponRequest { CouponId = coupon.Id, Version = coupon.Version }, TestCaller.Staff("catalog_manager").Call());
            }
            var key = Guid.NewGuid();

            await FailsAsync(Seed.CheckoutAsync(t, account, key, r => r.CouponCode = code), StatusCode.FailedPrecondition, reason);
            await FailsAsync(Seed.CheckoutAsync(t, account, key, r => r.CouponCode = code), StatusCode.FailedPrecondition, reason); // stored

            await AssertCancelledAsync(t, account);
        }

        // a coupon that applies to the plan only reaches no product line
        {
            var account = Guid.NewGuid();
            var device = Seed.Device(t.Upstreams);
            var plan = Seed.Plan(t.Upstreams, device);
            await Seed.CartAsync(t, account, (device, 1, null), (plan, 1, 1));
            await CouponAsync(t, "PLAN-1", r => { r.Scope = "selected"; r.ScopeItems.Add(new ScopeItem { TargetType = "product", TargetId = plan.ProductId }); });

            await FailsAsync(Seed.CheckoutAsync(t, account, configure: r => r.CouponCode = "PLAN-1"), StatusCode.FailedPrecondition, "COUPON_NOT_APPLICABLE");

            await AssertCancelledAsync(t, account);
        }

        // the per-customer limit: the first checkout holds the use, the second is refused before any hold
        {
            var (account, _) = await CartOfOneAsync(t);
            await CouponAsync(t, "ONCE-1", r => r.PerCustomerLimit = 1);
            await Seed.CheckoutAsync(t, account, configure: r => r.CouponCode = "ONCE-1");
            var holds = t.Upstreams.Count("ReserveStock");

            await FailsAsync(Seed.CheckoutAsync(t, account, configure: r => r.CouponCode = "ONCE-1"), StatusCode.FailedPrecondition, "COUPON_LIMIT_REACHED");

            Assert.Equal(holds, t.Upstreams.Count("ReserveStock"));
            await using var db = t.NewDb();
            var orders = await db.Orders.AsNoTracking().Where(o => o.AccountId == account).OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToListAsync(Ct);
            Assert.Equal(2, orders.Count);
            Assert.Equal(OrderStatuses.Cancelled, orders.Single(o => o.Status != OrderStatuses.PendingPayment).Status);
            Assert.Equal(1, await db.CouponUses.CountAsync(u => u.AccountId == account, Ct));
            Assert.Empty(await db.OrderSagas.AsNoTracking().Where(s => orders.Select(o => o.Id).Contains(s.OrderId)).ToListAsync(Ct));
        }
    }

    [Fact]
    public async Task Coupon_deactivated_while_held_still_completes()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t, priceCents: 1000);
        var coupon = await CouponAsync(t, "SAVE-10");
        t.Upstreams.Before = async method =>
        {
            if (method == "CreatePayment") // the use is held by now
                await t.Rpc.Staff.DeactivateCouponAsync(new DeactivateCouponRequest { CouponId = coupon.Id, Version = coupon.Version }, TestCaller.Staff("catalog_manager").Call());
        };

        var response = await Seed.CheckoutAsync(t, account, configure: r => r.CouponCode = "SAVE-10");

        Assert.Equal(100, response.DiscountCents);
        await using var db = t.NewDb();
        var stored = await db.Coupons.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((false, 1), (stored.Active, stored.UsesCount));
        Assert.Equal(CouponUseStatuses.Held, await db.CouponUses.AsNoTracking().Select(u => u.Status).SingleAsync(Ct));
        Assert.Equal(OrderStatuses.PendingPayment, (await OrderOfAsync(t, account)).Status);
    }

    [Fact]
    public async Task Locked_prices_survive_a_price_change()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, device) = await CartOfOneAsync(t, priceCents: 1000);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("CreatePayment", StatusCode.Unavailable);
        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));
        var before = await OrderOfAsync(t, account);
        var repriced = device.Clone();
        repriced.PriceCents = 5000;
        t.Upstreams.Sku(repriced);

        var response = await Seed.CheckoutAsync(t, account, key);

        Assert.Equal((1000L, 83L, 1083L), (response.SubtotalCents, response.TaxCents, response.TotalCents));
        var after = await OrderOfAsync(t, account);
        Assert.Equal((before.SubtotalCents, before.TotalCents), (after.SubtotalCents, after.TotalCents));
        await using var db = t.NewDb();
        Assert.Equal(1000, await db.OrderLines.AsNoTracking().Select(l => l.UnitPriceCents).SingleAsync(Ct));
        Assert.Equal(1, t.Upstreams.Count("GetPricing")); // the resumed order is not priced again
    }

    [Fact]
    public async Task Address_and_key_are_checked_before_any_call()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);

        var zip = await FailsAsync(Seed.CheckoutAsync(t, account, configure: r => r.ShipTo.Zip = "ABC"), StatusCode.InvalidArgument, "ADDRESS_INVALID");
        Assert.Equal("zip", Rpc.Info(zip).Metadata["field"]);
        var option = await FailsAsync(Seed.CheckoutAsync(t, account, configure: r => r.ShippingOption = "overnight"), StatusCode.InvalidArgument, "VALIDATION_FAILED");
        Assert.Equal("shipping_option", Rpc.Info(option).Metadata["field"]);
        var code = await FailsAsync(Seed.CheckoutAsync(t, account, configure: r => r.CouponCode = "x"), StatusCode.InvalidArgument, "VALIDATION_FAILED");
        Assert.Equal("coupon_code", Rpc.Info(code).Metadata["field"]);
        var noKey = t.Rpc.Checkout.StartCheckoutAsync(Seed.CheckoutRequest(), TestCaller.Customer(account).Call());
        await FailsAsync(noKey, StatusCode.InvalidArgument, "IDEMPOTENCY_KEY_REQUIRED");

        Assert.Empty(t.Upstreams.Calls);
        await using var db = t.NewDb();
        Assert.Empty(await db.Orders.ToListAsync(Ct));
        Assert.Empty(await db.CheckoutKeys.ToListAsync(Ct));
    }

    [Fact]
    public async Task Catalog_down_is_UNAVAILABLE_and_retry_safe()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t);
        var key = Guid.NewGuid();
        t.Upstreams.Fail("GetPricing", StatusCode.Unavailable);

        await UnavailableAsync(Seed.CheckoutAsync(t, account, key));

        var order = await OrderOfAsync(t, account);
        Assert.Equal(OrderStatuses.Creating, order.Status); // nothing cancelled, nothing held
        Assert.Equal(0, t.Upstreams.Count("ReserveStock"));
        Assert.Empty(await Outbox.PayloadsAsync<OrderCancelledV1>(t, OrderMessages.Cancelled));

        var response = await Seed.CheckoutAsync(t, account, key);

        Assert.Equal(order.Id.ToString("D"), response.OrderId);
        Assert.Equal(OrderStatuses.PendingPayment, (await OrderOfAsync(t, account)).Status);
    }

    [Fact]
    public async Task Express_shipping_adds_the_fee_line()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, _) = await CartOfOneAsync(t, priceCents: 1000);
        var body = new
        {
            cart = new { },
            shipTo = new { name = "Ada Lovelace", lines = new[] { "1 Main St" }, city = "Reno", state = "NV", zip = "89501", phone = "+17755550100" },
            shippingOption = "express",
        };

        using var http = await t.SendAsync(HttpMethod.Post, "/api/checkout", body, TestCaller.Customer(account), Guid.NewGuid());
        var json = await http.OkJsonAsync();

        Assert.Equal(("1500", "2583"), (json.GetProperty("shippingCents").GetString(), json.GetProperty("totalCents").GetString()));
        var payment = (CreatePaymentRequest)Assert.Single(t.Upstreams.Calls, c => c.Method == "CreatePayment").Request;
        Assert.Equal([("Phone 1000 × 1", 1083L), ("Express shipping", 1500L)], payment.DisplayLines.Select(l => (l.Name, l.AmountCents)));
        Assert.Equal(2583, payment.AmountCents);
        var order = await OrderOfAsync(t, account);
        Assert.Equal(("express", 1500L), (order.ShippingOption, order.ShippingCents));
    }

    [Fact]
    public async Task Coupon_hold_enforces_the_per_customer_limit_and_use_moves_undo_the_counts()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var coupon = await CouponAsync(t, "ONCE-1", r => { r.TotalLimit = 10; r.PerCustomerLimit = 1; });
        var account = Guid.NewGuid();
        var orders = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await using (var seed = t.NewDb())
        {
            seed.Orders.AddRange(orders.Select((id, i) => new OrderRow
            {
                Id = id, Number = $"BLM-TEST000{i}", AccountId = account, Kind = OrderKinds.Regular, Status = OrderStatuses.Creating, ShippingOption = "standard",
                ShipTo = new ShipToV1("n", ["l"], "c", "NV", "89501", "+17755550100"), Currency = "USD", CreatedAt = t.Clock.GetUtcNow(), UpdatedAt = t.Clock.GetUtcNow(),
            }));
            await seed.SaveChangesAsync(Ct);
        }

        await using (var scope = t.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var coupons = scope.ServiceProvider.GetRequiredService<Coupons>();
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            await coupons.HoldAsync(Guid.Parse(coupon.Id), account, orders[0], Ct);
            var e = await Assert.ThrowsAsync<RpcException>(() => coupons.HoldAsync(Guid.Parse(coupon.Id), account, orders[1], Ct));
            Assert.Equal("COUPON_LIMIT_REACHED", Rpc.Info(e).Reason);
            Assert.True(await coupons.MoveUseAsync(orders[0], CouponUseStatuses.Held, CouponUseStatuses.Released, Ct)); // both counters go back down
            Assert.False(await coupons.MoveUseAsync(orders[0], CouponUseStatuses.Held, CouponUseStatuses.Consumed, Ct)); // no longer held
            await tx.CommitAsync(Ct);
        }

        await using var check = t.NewDb();
        Assert.Equal(0, (await check.Coupons.AsNoTracking().SingleAsync(Ct)).UsesCount); // held once, released: the refused hold left no count behind
        Assert.Equal(0, await check.CouponCustomerUsages.AsNoTracking().Select(u => u.UsesCount).SingleAsync(Ct));
        var use = await check.CouponUses.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((orders[0], CouponUseStatuses.Released), (use.OrderId, use.Status));
    }
}
