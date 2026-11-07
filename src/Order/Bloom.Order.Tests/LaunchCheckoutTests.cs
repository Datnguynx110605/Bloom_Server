using Bloom.Catalog.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Data;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Tests;

public class LaunchCheckoutTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static CheckoutLine Product(SkuPricing sku, int quantity = 1) => new() { Kind = "product", SkuId = sku.SkuId, Quantity = quantity };

    private static CheckoutLine PlanOn(SkuPricing plan, int parentLineNo, int quantity = 1) =>
        new() { Kind = "warranty_plan", SkuId = plan.SkuId, Quantity = quantity, ParentLineNo = parentLineNo };

    private static AsyncUnaryCall<StartCheckoutResponse> LaunchCheckout(TestOrder t, Guid account, Guid launchId, Guid? key = null, Action<StartCheckoutRequest>? configure = null, params CheckoutLine[] lines) =>
        Seed.CheckoutAsync(t, account, key, r =>
        {
            r.Launch = new LaunchSource { LaunchId = launchId.ToString("D"), PassToken = "pass-1", Lines = { lines } };
            configure?.Invoke(r);
        });

    private static async Task<RpcException> FailsAsync(AsyncUnaryCall<StartCheckoutResponse> call, StatusCode code, string reason)
    {
        var e = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Rpc.Info(e).Reason);
        return e;
    }

    private static async Task<OrderRow> CancelledAsync(TestOrder t, Guid account)
    {
        await using var db = t.NewDb();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.AccountId == account, Ct);
        Assert.Equal((OrderStatuses.Cancelled, CancelReasons.CheckoutFailed), (order.Status, order.CancelReason));
        return order;
    }

    [Fact]
    public async Task Launch_checkout_holds_allocation_with_customer_context()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (account, launchId) = (Guid.NewGuid(), Guid.NewGuid());
        var a = Seed.LaunchSku(t.Upstreams, launchId, priceCents: 1000);
        var b = Seed.LaunchSku(t.Upstreams, launchId, priceCents: 2000);
        var plan = Seed.Plan(t.Upstreams, a, 500);

        // the plan is listed second but numbered after both devices (as the cart does), and keeps pointing at its device
        var response = await LaunchCheckout(t, account, launchId, lines: [Product(a, 2), PlanOn(plan, 1), Product(b)]);

        Assert.Equal((2000L + 2000 + 500, 0L), (response.SubtotalCents, response.DiscountCents));
        var hold = Assert.Single(t.Upstreams.Calls, c => c.Method == "HoldAllocation");
        var request = (HoldAllocationRequest)hold.Request;
        Assert.Equal((response.OrderId, "pass-1", account.ToString("D")), (request.OrderId, request.PassToken, hold.UserId));
        Assert.Equal([(a.SkuId, 2), (b.SkuId, 1)], request.Lines.Select(l => (l.SkuId, l.Quantity))); // product lines only
        Assert.Equal(0, t.Upstreams.Count("ReserveStock"));

        await using var db = t.NewDb();
        var order = await db.Orders.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((OrderStatuses.PendingPayment, OrderKinds.Launch, launchId), (order.Status, order.Kind, order.LaunchId));
        var lines = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == order.Id).OrderBy(l => l.LineNo).ToListAsync(Ct);
        Assert.Equal([(1, "product", (int?)null), (2, "product", null), (3, "warranty_plan", 1)], lines.Select(l => (l.LineNo, l.Kind, l.ParentLineNo)));
        Assert.Equal(1, t.Upstreams.Count("CreatePayment"));
        Assert.Equal(1, metrics.Sum("checkout_total", ("kind", "launch"), ("result", "ok")));
        Assert.Equal(1, metrics.Count("checkout_start_seconds", ("kind", "launch")));
    }

    [Fact]
    public async Task O17_launch_checkout_with_a_regular_sku_is_MIXED_LAUNCH_ORDER()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId) = (Guid.NewGuid(), Guid.NewGuid());
        var regular = Seed.Device(t.Upstreams);

        await FailsAsync(LaunchCheckout(t, account, launchId, lines: [Product(Seed.LaunchSku(t.Upstreams, launchId)), Product(regular)]), StatusCode.InvalidArgument, "MIXED_LAUNCH_ORDER");

        await CancelledAsync(t, account);
        Assert.Equal(0, t.Upstreams.Count("HoldAllocation"));
    }

    [Fact]
    public async Task Another_launchs_sku_is_MIXED_LAUNCH_ORDER()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId) = (Guid.NewGuid(), Guid.NewGuid());
        var other = Seed.LaunchSku(t.Upstreams, Guid.NewGuid());

        await FailsAsync(LaunchCheckout(t, account, launchId, lines: [Product(other)]), StatusCode.InvalidArgument, "MIXED_LAUNCH_ORDER");

        await CancelledAsync(t, account);
        Assert.Equal(0, t.Upstreams.Count("HoldAllocation"));
    }

    [Fact]
    public async Task O18_plan_quantity_2_on_device_quantity_1_is_WARRANTY_NOT_ELIGIBLE()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId) = (Guid.NewGuid(), Guid.NewGuid());
        var device = Seed.LaunchSku(t.Upstreams, launchId);
        var plan = Seed.Plan(t.Upstreams, device);

        await FailsAsync(LaunchCheckout(t, account, launchId, lines: [Product(device), PlanOn(plan, 1, 2)]), StatusCode.InvalidArgument, "WARRANTY_NOT_ELIGIBLE");

        await CancelledAsync(t, account);
        Assert.Equal(0, t.Upstreams.Count("HoldAllocation"));
    }

    [Fact]
    public async Task Launch_order_with_a_coupon_is_COUPON_NOT_APPLICABLE()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId) = (Guid.NewGuid(), Guid.NewGuid());
        await t.Rpc.Staff.CreateCouponAsync(new CreateCouponRequest { Code = "SAVE-10", Type = "percent", Value = 10, Scope = "order" }, TestCaller.Staff("catalog_manager").Call());

        await FailsAsync(LaunchCheckout(t, account, launchId, configure: r => r.CouponCode = "SAVE-10", lines: [Product(Seed.LaunchSku(t.Upstreams, launchId))]),
            StatusCode.FailedPrecondition, "COUPON_NOT_APPLICABLE");

        await CancelledAsync(t, account);
        Assert.Equal(0, t.Upstreams.Count("HoldAllocation"));
    }

    [Theory, InlineData("SOLD_OUT"), InlineData("LIMIT_EXCEEDED"), InlineData("PASS_EXPIRED")]
    public async Task Hold_rejection_passes_through_and_cancels(string reason)
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId, key) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var device = Seed.LaunchSku(t.Upstreams, launchId);
        t.Upstreams.Fail("HoldAllocation", StatusCode.FailedPrecondition, reason);

        await FailsAsync(LaunchCheckout(t, account, launchId, key, lines: [Product(device)]), StatusCode.FailedPrecondition, reason);

        var order = await CancelledAsync(t, account);
        await using var db = t.NewDb();
        Assert.Empty(await db.OrderSagas.ToListAsync(Ct)); // a new order holds nothing yet: no saga, no release
        Assert.Empty(await Outbox.PayloadsAsync<ReleaseAllocationHoldV1>(t, LaunchMessages.ReleaseAllocationHold));
        Assert.Equal(1, (await Outbox.PayloadsAsync<Bloom.Order.Contracts.OrderCancelledV1>(t, Bloom.Order.Contracts.OrderMessages.Cancelled)).Count(c => c.OrderId == order.Id));

        await FailsAsync(LaunchCheckout(t, account, launchId, key, lines: [Product(device)]), StatusCode.FailedPrecondition, reason); // replayed from the key
        Assert.Equal(1, t.Upstreams.Count("HoldAllocation"));
    }

    [Fact]
    public async Task Resumed_order_hold_rejection_releases_the_allocation_hold()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId, key) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var device = Seed.LaunchSku(t.Upstreams, launchId);
        t.Upstreams.Fail("HoldAllocation", StatusCode.Unavailable); // launch did hold: the call is recorded, then it answers badly
        Assert.Equal(StatusCode.Unavailable, (await Assert.ThrowsAsync<RpcException>(() => LaunchCheckout(t, account, launchId, key, lines: [Product(device)]).ResponseAsync)).StatusCode);
        t.Upstreams.Fail("HoldAllocation", StatusCode.FailedPrecondition, "PASS_EXPIRED");

        await FailsAsync(LaunchCheckout(t, account, launchId, key, lines: [Product(device)]), StatusCode.FailedPrecondition, "PASS_EXPIRED");

        var order = await CancelledAsync(t, account);
        var release = Assert.Single(await Outbox.PayloadsAsync<ReleaseAllocationHoldV1>(t, LaunchMessages.ReleaseAllocationHold));
        Assert.Equal((order.Id, "checkout_failed"), (release.OrderId, release.Reason));
        await using var db = t.NewDb();
        var saga = await db.OrderSagas.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((order.Id, SagaTypes.CheckoutCompensation), (saga.OrderId, saga.Type));
        Assert.Equal([SagaSteps.LaunchRelease], saga.PendingSteps);
    }

    [Fact]
    public async Task Preorder_open_sku_is_accepted_although_catalog_says_not_buyable()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId) = (Guid.NewGuid(), Guid.NewGuid());
        var device = Seed.LaunchSku(t.Upstreams, launchId);
        Assert.False(device.Buyable); // 08-catalog §6.1: the launch flow judges it, not buyable

        var response = await LaunchCheckout(t, account, launchId, lines: [Product(device)]);

        Assert.NotEmpty(response.PaymentUrl);
    }

    [Fact]
    public async Task Stage_not_open_is_SKU_NOT_BUYABLE()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId) = (Guid.NewGuid(), Guid.NewGuid());

        await FailsAsync(LaunchCheckout(t, account, launchId, lines: [Product(Seed.LaunchSku(t.Upstreams, launchId, stage: "released"))]), StatusCode.FailedPrecondition, "SKU_NOT_BUYABLE");

        await CancelledAsync(t, account);
        Assert.Equal(0, t.Upstreams.Count("HoldAllocation"));
    }

    [Fact]
    public async Task Launch_lines_are_validated_without_creating_an_order()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, launchId) = (Guid.NewGuid(), Guid.NewGuid());
        var device = Seed.LaunchSku(t.Upstreams, launchId);
        var plan = Seed.Plan(t.Upstreams, device);
        CheckoutLine[][] bad =
        [
            [], // empty
            [Product(device, 0)], // quantity below 1
            [PlanOn(plan, 1)], // no product line
            [Product(device), Product(device)], // duplicate
            [Product(device), PlanOn(plan, 1), PlanOn(plan, 1)], // duplicate plan
            [new CheckoutLine { Kind = "bundle", SkuId = device.SkuId, Quantity = 1 }], // unknown kind
            [new CheckoutLine { Kind = "product", SkuId = "x", Quantity = 1 }], // not a UUID
        ];
        foreach (var lines in bad)
        {
            var e = await FailsAsync(LaunchCheckout(t, account, launchId, lines: lines), StatusCode.InvalidArgument, "VALIDATION_FAILED");
            Assert.Equal("lines", Rpc.Info(e).Metadata["field"]);
        }

        await using var db = t.NewDb();
        Assert.Empty(await db.Orders.ToListAsync(Ct));
    }
}
