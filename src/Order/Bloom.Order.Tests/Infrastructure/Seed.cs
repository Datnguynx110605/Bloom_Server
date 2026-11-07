using Bloom.Catalog.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Data;
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
}
