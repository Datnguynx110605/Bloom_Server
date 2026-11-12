using System.Text.Json;
using Bloom.Catalog.Contracts;
using Bloom.Inventory.Contracts;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Tests;

public class CartTests(Containers containers)
{
    private static Task<HttpResponseMessage> GetAsync(TestOrder t, TestCaller caller) => t.SendAsync(HttpMethod.Get, "/api/cart", caller: caller);

    private static Task<HttpResponseMessage> UpsertAsync(TestOrder t, TestCaller caller, SkuPricing sku, int quantity, long version, int? parent = null)
    {
        var body = new Dictionary<string, object> { ["kind"] = sku.Kind, ["skuId"] = sku.SkuId, ["quantity"] = quantity, ["version"] = version };
        if (parent is { } p) body["parentLineNo"] = p;
        return t.SendAsync(HttpMethod.Put, "/api/cart/lines", body, caller);
    }

    private static Task<HttpResponseMessage> RemoveAsync(TestOrder t, TestCaller caller, int lineNo, long version) =>
        t.SendAsync(HttpMethod.Delete, $"/api/cart/lines/{lineNo}?version={version}", caller: caller);

    private static JsonElement[] Lines(JsonElement cart) => cart.TryGetProperty("lines", out var l) ? [.. l.EnumerateArray()] : [];

    private static string Version(JsonElement cart) => cart.GetProperty("version").GetString()!;

    private static SkuPricing With(FakeUpstreams u, SkuPricing sku, Action<SkuPricing> change)
    {
        var copy = sku.Clone();
        change(copy);
        u.Sku(copy);
        return copy;
    }

    [Fact]
    public async Task Empty_cart_has_version_0()
    {
        await using var t = await TestOrder.StartAsync(containers);

        using var response = await GetAsync(t, TestCaller.Customer());
        var cart = await response.OkJsonAsync();

        Assert.Equal("0", Version(cart));
        Assert.Empty(Lines(cart));
        Assert.Equal(0, t.Upstreams.Count("GetPricing")); // nothing to price
    }

    [Fact]
    public async Task Upsert_add_update_and_remove_by_zero()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var customer = TestCaller.Customer();
        var first = Seed.Device(t.Upstreams);
        var second = Seed.Device(t.Upstreams, 2500);

        using (var add = await UpsertAsync(t, customer, first, 2, 0))
        {
            var cart = await add.OkJsonAsync();
            Assert.Equal("1", Version(cart));
            var line = Assert.Single(Lines(cart));
            Assert.Equal(1, line.GetProperty("lineNo").GetInt32());
            Assert.Equal(2, line.GetProperty("quantity").GetInt32());
        }
        using (var update = await UpsertAsync(t, customer, first, 5, 1)) // absolute: 5, not 2 + 5
        {
            var cart = await update.OkJsonAsync();
            Assert.Equal("2", Version(cart));
            Assert.Equal(5, Assert.Single(Lines(cart)).GetProperty("quantity").GetInt32());
        }
        using (var other = await UpsertAsync(t, customer, second, 1, 2))
        {
            var cart = await other.OkJsonAsync();
            Assert.Equal("3", Version(cart));
            Assert.Equal([1, 2], Lines(cart).Select(l => l.GetProperty("lineNo").GetInt32()));
        }
        using (var remove = await UpsertAsync(t, customer, first, 0, 3))
        {
            var cart = await remove.OkJsonAsync();
            Assert.Equal("4", Version(cart));
            var line = Assert.Single(Lines(cart));
            Assert.Equal(2, line.GetProperty("lineNo").GetInt32());
            Assert.Equal(second.SkuId, line.GetProperty("skuId").GetString());
        }
        using (var again = await UpsertAsync(t, customer, first, 1, 4)) // a removed line comes back with the next number
            Assert.Equal(3, Assert.Single(Lines(await again.OkJsonAsync()), l => l.GetProperty("skuId").GetString() == first.SkuId).GetProperty("lineNo").GetInt32());
    }

    [Fact]
    public async Task Stale_version_is_CONCURRENCY_CONFLICT()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var customer = TestCaller.Customer();
        var device = Seed.Device(t.Upstreams);

        using (var nothing = await UpsertAsync(t, customer, device, 1, 3)) await nothing.ExpectErrorAsync(409, "CONCURRENCY_CONFLICT"); // no cart is version 0
        using (var first = await UpsertAsync(t, customer, device, 1, 0)) await first.OkJsonAsync();
        using (var again = await UpsertAsync(t, customer, device, 2, 0)) await again.ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        using (var ahead = await UpsertAsync(t, customer, device, 2, 2)) await ahead.ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        using (var remove = await RemoveAsync(t, customer, 1, 0)) await remove.ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");

        using var get = await GetAsync(t, customer);
        var cart = await get.OkJsonAsync();
        Assert.Equal("1", Version(cart)); // the rejected writes changed nothing
        Assert.Equal(1, Assert.Single(Lines(cart)).GetProperty("quantity").GetInt32());

        // Two first writes at the same time: the primary key lets one in.
        var racer = TestCaller.Customer();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var r = await UpsertAsync(t, racer, device, 1, 0);
            return (int)r.StatusCode;
        }));
        Assert.Equal(1, results.Count(s => s == 200));
        Assert.Equal(3, results.Count(s => s == 409));
    }

    [Fact]
    public async Task Get_cart_shows_current_price_and_flags()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var customer = TestCaller.Customer();
        var phone = Seed.Device(t.Upstreams);
        var plan = Seed.Plan(t.Upstreams, phone);
        var gone = Seed.Device(t.Upstreams, 500);
        var enough = Seed.Device(t.Upstreams, 700);
        await Seed.CartAsync(t, customer.Id, (phone, 2, null), (plan, 1, 1), (gone, 1, null), (enough, 1, null));
        t.Upstreams.Available(Guid.Parse(phone.SkuId), 1); // 1 in stock for quantity 2
        t.Upstreams.Available(Guid.Parse(enough.SkuId), 5);
        With(t.Upstreams, phone, s => s.PriceCents = 1500); // the catalog price changed since it was added
        With(t.Upstreams, gone, s => s.Buyable = false);

        using var response = await GetAsync(t, customer);
        var lines = Lines(await response.OkJsonAsync());

        Assert.Equal(4, lines.Length);
        Assert.Equal("1500", lines[0].GetProperty("priceCents").GetString());
        Assert.Equal(phone.Name, lines[0].GetProperty("name").GetString());
        Assert.True(lines[0].GetProperty("available").GetBoolean());
        Assert.True(lines[0].GetProperty("outOfStock").GetBoolean());
        Assert.Equal(1, lines[1].GetProperty("parentLineNo").GetInt32());
        Assert.Equal("9900", lines[1].GetProperty("priceCents").GetString());
        Assert.False(lines[1].TryGetProperty("outOfStock", out _)); // plans have no stock
        Assert.False(lines[2].GetProperty("available").GetBoolean());
        Assert.False(lines[3].GetProperty("outOfStock").GetBoolean());
        Assert.Single(t.Upstreams.Calls, c => c.Method == "GetPricing");
        Assert.Equal(new[] { phone.SkuId, gone.SkuId, enough.SkuId }.Order(), ((GetAvailabilityRequest)t.Upstreams.Calls.Single(c => c.Method == "GetAvailability").Request).SkuIds.Order());
    }

    [Fact]
    public async Task Flags_are_omitted_when_inventory_is_down()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var customer = TestCaller.Customer();
        var phone = Seed.Device(t.Upstreams);
        await Seed.CartAsync(t, customer.Id, (phone, 2, null));
        t.Upstreams.Fail("GetAvailability", StatusCode.Unavailable);

        using var response = await GetAsync(t, customer);
        var line = Assert.Single(Lines(await response.OkJsonAsync()));

        Assert.Equal("1000", line.GetProperty("priceCents").GetString());
        Assert.False(line.TryGetProperty("outOfStock", out _));
    }

    [Fact]
    public async Task Launch_sku_is_LAUNCH_ITEMS_IN_CART()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var launch = Seed.LaunchSku(t.Upstreams, Guid.NewGuid());

        using var response = await UpsertAsync(t, TestCaller.Customer(), launch, 1, 0);

        await response.ExpectErrorAsync(400, "LAUNCH_ITEMS_IN_CART");
    }

    [Fact]
    public async Task Upsert_rules()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var customer = TestCaller.Customer();
        var device = Seed.Device(t.Upstreams);
        var plan = Seed.Plan(t.Upstreams, device);
        var closed = With(t.Upstreams, Seed.Device(t.Upstreams), s => s.Buyable = false);

        using (var many = await UpsertAsync(t, customer, device, 11, 0)) await many.ExpectErrorAsync(400, "QUANTITY_LIMIT");
        using (var ten = await UpsertAsync(t, customer, device, 10, 0)) await ten.OkJsonAsync();
        using (var notBuyable = await UpsertAsync(t, customer, closed, 1, 1)) await notBuyable.ExpectErrorAsync(400, "SKU_NOT_BUYABLE");
        using (var unknown = await UpsertAsync(t, customer, new SkuPricing { SkuId = Guid.NewGuid().ToString("D"), Kind = "product" }, 1, 1)) await unknown.ExpectErrorAsync(400, "SKU_NOT_BUYABLE");
        using (var mismatch = await UpsertAsync(t, customer, new SkuPricing { SkuId = plan.SkuId, Kind = "product" }, 1, 1)) await mismatch.ExpectValidationAsync("kind");
        using (var badKind = await UpsertAsync(t, customer, new SkuPricing { SkuId = device.SkuId, Kind = "gadget" }, 1, 1)) await badKind.ExpectValidationAsync("kind");
        using (var badSku = await UpsertAsync(t, customer, new SkuPricing { SkuId = "x", Kind = "product" }, 1, 1)) await badSku.ExpectValidationAsync("sku_id");
        using (var negative = await UpsertAsync(t, customer, device, -1, 1)) await negative.ExpectValidationAsync("quantity");
        using (var parentOnProduct = await UpsertAsync(t, customer, device, 1, 1, parent: 1)) await parentOnProduct.ExpectValidationAsync("parent_line_no");
        using (var planWithoutParent = await UpsertAsync(t, customer, plan, 1, 1)) await planWithoutParent.ExpectValidationAsync("parent_line_no");

        using var get = await GetAsync(t, customer);
        Assert.Equal("1", Version(await get.OkJsonAsync()));
    }

    [Fact]
    public async Task Plan_rules()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var customer = TestCaller.Customer();
        var phone = Seed.Device(t.Upstreams);
        var other = Seed.Device(t.Upstreams);
        var plan = Seed.Plan(t.Upstreams, phone);
        var otherPlan = Seed.Plan(t.Upstreams, other);
        await Seed.CartAsync(t, customer.Id, (phone, 1, null), (other, 2, null));

        using (var ineligible = await UpsertAsync(t, customer, otherPlan, 1, 1, parent: 1)) await ineligible.ExpectErrorAsync(400, "WARRANTY_NOT_ELIGIBLE");
        using (var tooMany = await UpsertAsync(t, customer, plan, 2, 1, parent: 1)) await tooMany.ExpectErrorAsync(400, "WARRANTY_NOT_ELIGIBLE"); // plan 2 on device 1
        using (var noParent = await UpsertAsync(t, customer, plan, 1, 1, parent: 9)) await noParent.ExpectValidationAsync("parent_line_no");

        using (var add = await UpsertAsync(t, customer, otherPlan, 2, 1, parent: 2)) // plan 2 on device 2 is fine
        {
            var cart = await add.OkJsonAsync();
            Assert.Equal("2", Version(cart));
            var line = Lines(cart)[2];
            Assert.Equal(3, line.GetProperty("lineNo").GetInt32());
            Assert.Equal(2, line.GetProperty("parentLineNo").GetInt32());
        }
        using (var asParent = await UpsertAsync(t, customer, otherPlan, 1, 2, parent: 3)) await asParent.ExpectValidationAsync("parent_line_no"); // a plan is no parent
        using (var lowered = await UpsertAsync(t, customer, other, 1, 2)) await lowered.ExpectErrorAsync(400, "WARRANTY_NOT_ELIGIBLE"); // device below its plan
        using (var kept = await UpsertAsync(t, customer, other, 2, 2)) await kept.OkJsonAsync();

        using var get = await GetAsync(t, customer);
        Assert.Equal("3", Version(await get.OkJsonAsync()));
    }

    [Fact]
    public async Task Removing_a_device_removes_its_plans()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var phone = Seed.Device(t.Upstreams);
        var plan = Seed.Plan(t.Upstreams, phone);
        var tablet = Seed.Device(t.Upstreams);

        var viaRemove = TestCaller.Customer();
        await Seed.CartAsync(t, viaRemove.Id, (phone, 1, null), (plan, 1, 1), (tablet, 1, null));
        using (var remove = await RemoveAsync(t, viaRemove, 1, 1))
        {
            var cart = await remove.OkJsonAsync();
            Assert.Equal("2", Version(cart));
            Assert.Equal([3], Lines(cart).Select(l => l.GetProperty("lineNo").GetInt32()));
        }
        using (var unknown = await RemoveAsync(t, viaRemove, 1, 2)) await unknown.ExpectErrorAsync(404, "NOT_FOUND");
        using (var stale = await RemoveAsync(t, viaRemove, 3, 1)) await stale.ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");

        var viaZero = TestCaller.Customer();
        await Seed.CartAsync(t, viaZero.Id, (phone, 1, null), (plan, 1, 1), (tablet, 1, null));
        using (var zero = await UpsertAsync(t, viaZero, phone, 0, 1))
        {
            var cart = await zero.OkJsonAsync();
            Assert.Equal("2", Version(cart));
            Assert.Equal([3], Lines(cart).Select(l => l.GetProperty("lineNo").GetInt32()));
        }

        await using var db = t.NewDb();
        Assert.Equal(2, await db.CartLines.CountAsync(l => l.AccountId == viaRemove.Id || l.AccountId == viaZero.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Staff_cannot_use_the_cart()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var admin = TestCaller.Staff("admin");
        var device = Seed.Device(t.Upstreams);

        using (var get = await GetAsync(t, admin)) await get.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        using (var upsert = await UpsertAsync(t, admin, device, 1, 0)) await upsert.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        using (var invalid = await t.SendAsync(HttpMethod.Put, "/api/cart/lines", new { kind = "gadget", skuId = "x", quantity = -1, version = 0 }, admin))
            await invalid.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED"); // the role check runs before validation
        using (var remove = await RemoveAsync(t, admin, 1, 0)) await remove.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }
}
