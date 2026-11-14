using Bloom.Catalog.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Consumers;
using Bloom.Order.Data;
using Bloom.Payment.Contracts;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Cart = Bloom.Order.Data.Cart;
using CartLine = Bloom.Order.Data.CartLine;

namespace Bloom.Order.Tests;

/// <summary>Catalog entries on the <see cref="FakeUpstreams"/> and direct row writes for tests.</summary>
public static class Seed
{
    /// <summary>A buyable product SKU at <paramref name="priceCents"/>, added to the fake catalog.</summary>
    public static SkuPricing Device(FakeUpstreams u, long priceCents = 1000, Guid? categoryId = null, bool serialized = true)
    {
        var sku = new SkuPricing
        {
            SkuId = Guid.NewGuid().ToString("D"), PriceCents = priceCents, Name = $"Phone {priceCents}", ProductId = Guid.NewGuid().ToString("D"),
            CategoryId = (categoryId ?? Guid.NewGuid()).ToString("D"), Kind = LineKinds.Product, Serialized = serialized, Buyable = true,
        };
        u.Sku(sku);
        return sku;
    }

    /// <summary>A buyable warranty plan for the device's product, added to the fake catalog.</summary>
    public static SkuPricing Plan(FakeUpstreams u, SkuPricing device, long priceCents = 9900, int months = 12)
    {
        var sku = new SkuPricing
        {
            SkuId = Guid.NewGuid().ToString("D"), PriceCents = priceCents, Name = $"Plan {months} months", ProductId = Guid.NewGuid().ToString("D"),
            CategoryId = device.CategoryId, Kind = LineKinds.WarrantyPlan, Buyable = true, CoverageMonths = months, EligibleProductIds = { device.ProductId },
        };
        u.Sku(sku);
        return sku;
    }

    /// <summary>A product SKU that belongs to a launch (never allowed in the cart); <c>buyable</c> only once <c>released</c>, as catalog answers.</summary>
    public static SkuPricing LaunchSku(FakeUpstreams u, Guid launchId, string stage = "preorder_open", long priceCents = 99900)
    {
        var sku = Device(u, priceCents);
        sku.Buyable = stage == "released"; // 08-catalog §6.1: not buyable in the regular flow before the release
        sku.Launch = new LaunchInfo { LaunchId = launchId.ToString("D"), Stage = stage };
        u.Sku(sku);
        return sku;
    }

    /// <summary>
    /// Writes the account's cart straight to the database at version 1, lines numbered 1, 2, … in the order given. <c>Parent</c> is the <c>line_no</c> of the device line of a plan.
    /// </summary>
    public static async Task CartAsync(TestOrder t, Guid account, params (SkuPricing Sku, int Qty, int? Parent)[] lines)
    {
        await using var db = t.NewDb();
        db.Carts.Add(new Cart { AccountId = account, Version = 1, UpdatedAt = DateTimeOffset.UtcNow });
        db.CartLines.AddRange(lines.Select((l, i) => new CartLine
        {
            AccountId = account, LineNo = i + 1, Kind = l.Sku.Kind, SkuId = Guid.Parse(l.Sku.SkuId), ParentLineNo = l.Parent, Quantity = l.Qty,
        }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A valid regular checkout request: the caller's cart, a US address, standard shipping.</summary>
    public static StartCheckoutRequest CheckoutRequest(Action<StartCheckoutRequest>? configure = null)
    {
        var request = new StartCheckoutRequest
        {
            Cart = new CartSource(),
            ShipTo = new ShipTo { Name = "Ada Lovelace", Lines = { "1 Main St" }, City = "Reno", State = "NV", Zip = "89501", Phone = "+17755550100" },
            ShippingOption = "standard",
        };
        configure?.Invoke(request);
        return request;
    }

    /// <summary>Calls <c>StartCheckout</c> as the customer with an <c>Idempotency-Key</c> (a new one when <paramref name="key"/> is null). Await it for the response.</summary>
    public static AsyncUnaryCall<StartCheckoutResponse> CheckoutAsync(TestOrder t, Guid account, Guid? key = null, Action<StartCheckoutRequest>? configure = null)
    {
        var options = TestCaller.Customer(account).Call();
        options.Headers!.Add("idempotency-key", (key ?? Guid.NewGuid()).ToString("D"));
        return t.Rpc.Checkout.StartCheckoutAsync(CheckoutRequest(configure), options);
    }

    /// <summary>A launch checkout of one new launch SKU (1000 cents) x <paramref name="quantity"/>, left at <c>pending_payment</c>.</summary>
    public static async Task<StartCheckoutResponse> LaunchCheckoutAsync(TestOrder t, Guid account, Guid launchId, int quantity)
    {
        var device = LaunchSku(t.Upstreams, launchId, priceCents: 1000);
        return await CheckoutAsync(t, account, configure: r =>
            r.Launch = new LaunchSource { LaunchId = launchId.ToString("D"), PassToken = "pass-1", Lines = { new CheckoutLine { Kind = "product", SkuId = device.SkuId, Quantity = quantity } } });
    }

    /// <summary>Consumes <c>payment.succeeded.v1</c> for the order, as payment sends it.</summary>
    public static Task SucceededAsync(TestOrder t, Guid orderId, Guid? messageId = null) =>
        Consume.Async<PaymentSucceededConsumer>(t, PaymentMessages.Succeeded, new PaymentSucceededV1(orderId, FakeUpstreams.PaymentIdOf(orderId.ToString("D")), 0, t.Clock.GetUtcNow()), messageId);

    /// <summary>
    /// Lets <paramref name="by"/> pass for the sweeps: they compare <c>deadline_at</c> with the database clock, which the fake clock does not drive, so every order's deadline moves back instead.
    /// </summary>
    public static async Task AgeDeadlinesAsync(TestOrder t, TimeSpan by)
    {
        await using var db = t.NewDb();
        await db.Database.ExecuteSqlAsync($"UPDATE orders SET deadline_at = deadline_at - {by}", TestContext.Current.CancellationToken);
    }

    public static async Task<OrderRow> OrderAsync(TestOrder t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id, TestContext.Current.CancellationToken);
    }

    public static Task<OrderRow> OrderAsync(TestOrder t, string id) => OrderAsync(t, Guid.Parse(id));

    public static async Task<List<OrderStatusHistory>> HistoryAsync(TestOrder t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == id).OrderBy(h => h.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    public static Task<List<OrderStatusHistory>> HistoryAsync(TestOrder t, string id) => HistoryAsync(t, Guid.Parse(id));

    public static async Task SetStatusAsync(TestOrder t, Guid id, string status)
    {
        await using var db = t.NewDb();
        await db.Database.ExecuteSqlAsync($"UPDATE orders SET status = {status} WHERE id = {id}", TestContext.Current.CancellationToken);
    }

    public static Task SetStatusAsync(TestOrder t, string id, string status) => SetStatusAsync(t, Guid.Parse(id), status);

    public static async Task<OrderSaga> SagaAsync(TestOrder t, Guid orderId, string type)
    {
        await using var db = t.NewDb();
        return await db.Set<OrderSaga>().AsNoTracking().SingleAsync(s => s.OrderId == orderId && s.Type == type, TestContext.Current.CancellationToken);
    }

    public static async Task<int> OutboxCountAsync(TestOrder t)
    {
        await using var db = t.NewDb();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM outbox_messages").SingleAsync(TestContext.Current.CancellationToken);
    }
}
