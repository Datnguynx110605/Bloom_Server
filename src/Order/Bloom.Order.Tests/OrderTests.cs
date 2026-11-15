using Bloom.Order.Api;
using Bloom.Order.Data;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using static Bloom.Order.Tests.Seed;

namespace Bloom.Order.Tests;

public class OrderTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    /// <summary>A pending-payment order of one device (2 x 1000) and one plan (9900) for the account.</summary>
    private static async Task<StartCheckoutResponse> CheckoutAsync(TestOrder t, Guid account)
    {
        var device = Seed.Device(t.Upstreams, 1000);
        var plan = Seed.Plan(t.Upstreams, device, 9900);
        await using (var db = t.NewDb()) // an account's earlier checkout leaves its cart behind
        {
            await db.CartLines.Where(l => l.AccountId == account).ExecuteDeleteAsync(Ct);
            await db.Carts.Where(c => c.AccountId == account).ExecuteDeleteAsync(Ct);
        }
        await Seed.CartAsync(t, account, (device, 2, null), (plan, 1, 1));
        return await Seed.CheckoutAsync(t, account);
    }

    [Fact]
    public async Task Customer_reads_own_order_with_lines_and_history()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var account = Guid.NewGuid();
        var started = await CheckoutAsync(t, account);

        var order = await t.Rpc.Customer.GetOrderAsync(new GetOrderRequest { OrderId = started.OrderId }, TestCaller.Customer(account).Call());

        Assert.Equal((started.OrderId, started.Number, account.ToString("D"), "regular", "", "pending_payment", ""),
            (order.Id, order.Number, order.AccountId, order.Kind, order.LaunchId, order.Status, order.CancelReason));
        Assert.Equal(("Ada Lovelace", "Reno", "NV", "standard", "USD"), (order.ShipTo.Name, order.ShipTo.City, order.ShipTo.State, order.ShippingOption, order.Currency));
        Assert.Equal((started.SubtotalCents, started.DiscountCents, started.TaxCents, started.ShippingCents, started.TotalCents, 825),
            (order.SubtotalCents, order.DiscountCents, order.TaxCents, order.ShippingCents, order.TotalCents, order.TaxRateBp));
        Assert.Equal(started.DeadlineAt, order.DeadlineAt);
        Assert.Null(order.DeliveredAt);
        Assert.Equal([(1, "product", 2, 2000L, (int?)null), (2, "warranty_plan", 1, 9900L, 1)],
            order.Lines.Select(l => (l.LineNo, l.Kind, l.Quantity, l.UnitPriceCents * l.Quantity, l.HasParentLineNo ? l.ParentLineNo : (int?)null)));
        Assert.Equal(order.TotalCents - order.ShippingCents, order.Lines.Sum(l => l.PaidCents));
        Assert.Equal([("", "creating"), ("creating", "pending_payment")], order.History.Select(h => (h.FromStatus, h.ToStatus)));
        Assert.Equal(("customer", account.ToString("D")), (order.History[1].ActorType, order.History[1].ActorId));
    }

    /// <summary>A pending order with three more history rows: the system's (a reason code), staff's (a note) and the customer's own.</summary>
    private static async Task<(Guid Account, Guid OrderId, Guid StaffId)> OrderWithMixedHistoryAsync(TestOrder t)
    {
        var account = Guid.NewGuid();
        var orderId = Guid.Parse((await CheckoutAsync(t, account)).OrderId);
        var staffId = Guid.NewGuid();
        var at = t.Clock.GetUtcNow();
        await using var db = t.NewDb();
        db.OrderStatusHistory.AddRange(
            new OrderStatusHistory { OrderId = orderId, FromStatus = "pending_payment", ToStatus = "cancelled", ActorType = ActorTypes.System, Reason = "checkout_expired", At = at },
            new OrderStatusHistory { OrderId = orderId, FromStatus = "cancelled", ToStatus = "cancelled", ActorType = ActorTypes.Staff, ActorId = staffId, Reason = "Fraud suspected", At = at },
            new OrderStatusHistory { OrderId = orderId, FromStatus = "cancelled", ToStatus = "cancelled", ActorType = ActorTypes.Customer, ActorId = account, Reason = "changed_mind", At = at });
        await db.SaveChangesAsync(Ct);
        return (account, orderId, staffId);
    }

    [Fact]
    public async Task D22_customer_history_hides_staff_actor_and_reason()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, orderId, _) = await OrderWithMixedHistoryAsync(t);

        var order = await t.Rpc.Customer.GetOrderAsync(new GetOrderRequest { OrderId = orderId.ToString("D") }, TestCaller.Customer(account).Call());

        Assert.Equal(
            [("", "creating", "customer", account.ToString("D"), ""), ("creating", "pending_payment", "customer", account.ToString("D"), ""),
             ("pending_payment", "cancelled", "system", "", "checkout_expired"), // system rows keep their reason codes
             ("cancelled", "cancelled", "staff", "", ""), // no staff id, no staff note
             ("cancelled", "cancelled", "customer", account.ToString("D"), "changed_mind")], // the customer's own row is whole
            order.History.Select(h => (h.FromStatus, h.ToStatus, h.ActorType, h.ActorId, h.Reason)));
        Assert.All(order.History, h => Assert.NotNull(h.At));
    }

    [Fact]
    public async Task D22_support_sees_everything()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, orderId, staffId) = await OrderWithMixedHistoryAsync(t);

        foreach (var caller in new[] { TestCaller.Staff("support"), TestCaller.Staff("admin") })
        {
            var order = await t.Rpc.Staff.GetOrderAsync(new GetOrderRequest { OrderId = orderId.ToString("D") }, caller.Call());

            Assert.Equal(
                [("customer", account.ToString("D"), ""), ("customer", account.ToString("D"), ""), ("system", "", "checkout_expired"),
                 ("staff", staffId.ToString("D"), "Fraud suspected"), ("customer", account.ToString("D"), "changed_mind")],
                order.History.Select(h => (h.ActorType, h.ActorId, h.Reason)));
        }
    }

    [Fact]
    public async Task Other_customers_and_creating_orders_are_NOT_FOUND()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var account = Guid.NewGuid();
        var mine = await CheckoutAsync(t, account);
        var other = await CheckoutAsync(t, Guid.NewGuid());
        var creating = await CheckoutAsync(t, account);
        await SetStatusAsync(t, creating.OrderId, "creating");
        var me = TestCaller.Customer(account);

        var unknown = await Assert.ThrowsAsync<RpcException>(() => t.Rpc.Customer.GetOrderAsync(new GetOrderRequest { OrderId = Guid.NewGuid().ToString("D") }, me.Call()).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        foreach (var id in new[] { other.OrderId, creating.OrderId })
        {
            var read = await Assert.ThrowsAsync<RpcException>(() => t.Rpc.Customer.GetOrderAsync(new GetOrderRequest { OrderId = id }, me.Call()).ResponseAsync);
            Assert.Equal((unknown.StatusCode, unknown.Status.Detail), (read.StatusCode, read.Status.Detail));
            var cancel = await Assert.ThrowsAsync<RpcException>(() => t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = id }, me.Call()).ResponseAsync);
            Assert.Equal((unknown.StatusCode, unknown.Status.Detail), (cancel.StatusCode, cancel.Status.Detail));
            Assert.Equal(unknown.GetRpcStatus()!.Details.Single().Value, read.GetRpcStatus()!.Details.Single().Value);
        }

        var list = await t.Rpc.Customer.ListMyOrdersAsync(new ListMyOrdersRequest(), me.Call());
        Assert.Equal([mine.OrderId], list.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task List_my_orders_pages_newest_first()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var account = Guid.NewGuid();
        var ids = new List<string>();
        var before = await t.DbNowAsync();
        for (var i = 0; i < 3; i++) ids.Add((await CheckoutAsync(t, account)).OrderId); // created_at is the database clock, so newest first needs no clock moves
        var after = await t.DbNowAsync();
        var me = TestCaller.Customer(account);

        var first = await t.Rpc.Customer.ListMyOrdersAsync(new ListMyOrdersRequest { Page = 1, PageSize = 2 }, me.Call());
        var second = await t.Rpc.Customer.ListMyOrdersAsync(new ListMyOrdersRequest { Page = 2, PageSize = 2 }, me.Call());

        Assert.Equal([ids[2], ids[1]], first.Items.Select(i => i.Id));
        Assert.Equal([ids[0]], second.Items.Select(i => i.Id));
        var summary = first.Items[0];
        Assert.Equal(("regular", "pending_payment"), (summary.Kind, summary.Status));
        Assert.StartsWith("BLM-", summary.Number);
        Assert.True(summary.TotalCents > 0);
        Assert.InRange(summary.CreatedAt.ToDateTimeOffset(), before, after);
        Assert.Equal(3, (await t.Rpc.Customer.ListMyOrdersAsync(new ListMyOrdersRequest(), me.Call())).Items.Count); // page 0 is 1, page_size 0 is 50
    }

    [Fact]
    public async Task Support_searches_by_number_account_and_status()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var a1 = await CheckoutAsync(t, a);
        var a2 = await CheckoutAsync(t, a);
        var b1 = await CheckoutAsync(t, b);
        await SetStatusAsync(t, a2.OrderId, "paid");
        var support = TestCaller.Staff("support");

        async Task<string[]> SearchAsync(SearchOrdersRequest r) => (await t.Rpc.Staff.SearchOrdersAsync(r, support.Call())).Items.Select(i => i.Id).ToArray();

        Assert.Equal([b1.OrderId, a2.OrderId, a1.OrderId], await SearchAsync(new SearchOrdersRequest())); // newest first
        Assert.Equal([a1.OrderId], await SearchAsync(new SearchOrdersRequest { Number = a1.Number }));
        Assert.Equal([a2.OrderId, a1.OrderId], await SearchAsync(new SearchOrdersRequest { AccountId = a.ToString("D") }));
        Assert.Equal([a2.OrderId], await SearchAsync(new SearchOrdersRequest { AccountId = a.ToString("D"), Status = "paid" }));
        Assert.Equal([b1.OrderId, a1.OrderId], await SearchAsync(new SearchOrdersRequest { Status = "pending_payment" }));
        Assert.Empty(await SearchAsync(new SearchOrdersRequest { Number = a1.Number, AccountId = b.ToString("D") }));
        Assert.Equal([a1.OrderId], await SearchAsync(new SearchOrdersRequest { Page = 2, PageSize = 1, AccountId = a.ToString("D") }));

        var order = await t.Rpc.Staff.GetOrderAsync(new GetOrderRequest { OrderId = b1.OrderId }, TestCaller.Staff("admin").Call()); // any order
        Assert.Equal((b1.Number, b.ToString("D")), (order.Number, order.AccountId));
        Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(() => t.Rpc.Staff.GetOrderAsync(new GetOrderRequest { OrderId = Guid.NewGuid().ToString("D") }, support.Call()).ResponseAsync)).StatusCode);

        var bad = await Assert.ThrowsAsync<RpcException>(() => t.Rpc.Staff.SearchOrdersAsync(new SearchOrdersRequest { Status = "shipping" }, support.Call()).ResponseAsync);
        Assert.Equal("status", Rpc.Info(bad).Metadata["field"]);
        Assert.Equal("VALIDATION_FAILED", Rpc.Info(bad).Reason);
        await Rpc.FailsAsync(t.Rpc.Staff.SearchOrdersAsync(new SearchOrdersRequest { Status = "shipping" }, TestCaller.Staff("catalog_manager").Call()), "ROLE_NOT_ALLOWED"); // the role is checked first
        await Rpc.FailsAsync(t.Rpc.Staff.GetOrderAsync(new GetOrderRequest { OrderId = a1.OrderId }, TestCaller.Customer(a).Call()), "ROLE_NOT_ALLOWED");
    }
}
