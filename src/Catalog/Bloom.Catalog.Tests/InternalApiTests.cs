using System.Net;
using System.Net.Http.Json;
using Bloom.Catalog.Contracts;
using Bloom.Catalog.Data;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Tests;

public class InternalApiTests(Containers containers)
{
    private static readonly TestCaller Manager = TestCaller.Staff("catalog_manager");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<GetPricingResponse> PricingAsync(TestCatalog t, params string[] ids) =>
        t.Internal.GetPricingAsync(new GetPricingRequest { SkuIds = { ids } }, cancellationToken: Ct).ResponseAsync;

    private static async Task<SkuPricing> PriceOneAsync(TestCatalog t, Guid sku) => Assert.Single((await PricingAsync(t, sku.ToString())).Items);

    private static Task<GetSkuResponse> SkuAsync(TestCatalog t, string id) =>
        t.Internal.GetSkuAsync(new GetSkuRequest { SkuId = id }, cancellationToken: Ct).ResponseAsync;

    private static Task<LockSkuForStockResponse> LockAsync(TestCatalog t, string id) =>
        t.Internal.LockSkuForStockAsync(new LockSkuForStockRequest { SkuId = id }, cancellationToken: Ct).ResponseAsync;

    private static async Task ExpectNotFoundAsync(Func<Task> call) => Assert.Equal(StatusCode.NotFound, (await Assert.ThrowsAsync<RpcException>(call)).StatusCode);

    private static async Task SetProductAsync(TestCatalog t, Guid productId, string? stage, Guid? launchId = null)
    {
        await using var db = t.NewDb();
        await db.Products.Where(p => p.Id == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LaunchStage, stage).SetProperty(p => p.LaunchId, launchId), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetPricing_returns_the_order_fields()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        await using (var db = t.NewDb())
            await db.Products.Where(p => p.Id == phone.Product.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Serialized, true), TestContext.Current.CancellationToken);
        var (plan, planSku) = await Seed.PlanAsync(t, category.Id, [phone.Product.Id], 12_900, 24);
        var device = phone.Sku(phone.Storage256, phone.Black);

        var response = await PricingAsync(t, device.Id.ToString(), planSku.Id.ToString());

        Assert.Equal(1, metrics.Count("get_pricing_seconds"));
        var item = response.Items[0];
        Assert.Equal(device.Id.ToString(), item.SkuId);
        Assert.Equal(99_900, item.PriceCents);
        Assert.Equal("Bloom Phone Pro Max (256 GB, Black)", item.Name);
        Assert.Equal(phone.Product.Id.ToString(), item.ProductId);
        Assert.Equal(category.Id.ToString(), item.CategoryId);
        Assert.Equal("product", item.Kind);
        Assert.True(item.Serialized);
        Assert.True(item.Buyable);
        Assert.Null(item.Launch);
        Assert.Empty(item.EligibleProductIds);
        Assert.Equal(0, item.CoverageMonths);

        var planItem = response.Items[1];
        Assert.Equal("warranty_plan", planItem.Kind);
        Assert.Equal(plan.Name, planItem.Name);
        Assert.Equal(12_900, planItem.PriceCents);
        Assert.Equal([phone.Product.Id.ToString()], planItem.EligibleProductIds);
        Assert.Equal(24, planItem.CoverageMonths);
        Assert.False(planItem.Serialized);
        Assert.True(planItem.Buyable);
    }

    [Fact]
    public async Task C05_GetPricing_right_after_price_change_has_new_price()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, skus) = await Seed.ProductAsync(t, category.Id);
        await t.Redis.StringSetAsync($"product:{product.Id}", "not a ProductView"); // would fail to parse if read

        Assert.Equal(99_900, (await PriceOneAsync(t, skus[0].Id)).PriceCents);
        await (await t.SendAsync(HttpMethod.Put, $"/api/admin/catalog/skus/{skus[0].Id}/price", new { priceCents = 88_800, version = "1" }, Manager)).OkJsonAsync();

        Assert.Equal(88_800, (await PriceOneAsync(t, skus[0].Id)).PriceCents);
    }

    [Fact]
    public async Task GetPricing_never_touches_redis()
    {
        await using var t = await TestCatalog.StartAsync(containers, o => o.Config["Catalog:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200");
        var category = await Seed.CategoryAsync(t);
        var (product, skus) = await Seed.ProductAsync(t, category.Id);

        Assert.True((await PriceOneAsync(t, skus[0].Id)).Buyable);
        Assert.Equal(product.Id.ToString(), (await SkuAsync(t, skus[0].Id.ToString())).ProductId);
        Assert.Equal(product.Id.ToString(), (await LockAsync(t, skus[0].Id.ToString())).ProductId);
    }

    [Fact]
    public async Task Unknown_and_malformed_ids_are_not_buyable()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (_, skus) = await Seed.ProductAsync(t, category.Id);
        var unknown = Guid.NewGuid().ToString();

        var response = await PricingAsync(t, unknown, "x", skus[0].Id.ToString(), skus[0].Id.ToString());

        Assert.Equal([unknown, "x", skus[0].Id.ToString()], response.Items.Select(i => i.SkuId));
        foreach (var item in response.Items.Take(2))
        {
            Assert.False(item.Buyable);
            Assert.False(item.HasPriceCents);
            Assert.Equal("", item.ProductId);
        }
        Assert.True(response.Items[2].Buyable);
    }

    [Theory, InlineData("draft"), InlineData("archived")]
    public async Task Non_active_product_is_not_buyable(string status)
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (_, skus) = await Seed.ProductAsync(t, category.Id, status);

        var item = await PriceOneAsync(t, skus[0].Id);

        Assert.False(item.Buyable);
        Assert.Equal(99_900, item.PriceCents); // known SKUs keep their facts
    }

    [Fact]
    public async Task Inactive_sku_is_not_buyable()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (_, skus) = await Seed.ProductAsync(t, category.Id);
        await using (var db = t.NewDb())
            await db.Skus.Where(s => s.Id == skus[0].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), TestContext.Current.CancellationToken);

        Assert.False((await PriceOneAsync(t, skus[0].Id)).Buyable);
    }

    [Fact]
    public async Task Launch_stage_controls_buyable()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, skus) = await Seed.ProductAsync(t, category.Id);
        var launch = Guid.NewGuid();

        foreach (var stage in new[] { "announced", "preorder_open" })
        {
            await SetProductAsync(t, product.Id, stage, launch);
            var item = await PriceOneAsync(t, skus[0].Id);
            Assert.False(item.Buyable);
            Assert.Equal(launch.ToString(), item.Launch.LaunchId);
            Assert.Equal(stage, item.Launch.Stage);
        }

        await SetProductAsync(t, product.Id, "released", launch);
        var released = await PriceOneAsync(t, skus[0].Id);
        Assert.True(released.Buyable);
        Assert.Equal("released", released.Launch.Stage);
    }

    [Fact]
    public async Task GetSku_returns_launch_fields()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, skus) = await Seed.ProductAsync(t, category.Id, skuPrices: [99_900, 89_900]);
        var launch = Guid.NewGuid();
        await using (var db = t.NewDb())
            await db.Skus.Where(s => s.Id == skus[0].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LaunchId, launch), TestContext.Current.CancellationToken);

        var response = await SkuAsync(t, skus[0].Id.ToString());

        Assert.Equal(product.Id.ToString(), response.ProductId);
        Assert.Equal("active", response.Status);
        Assert.True(response.Active);
        Assert.Equal(launch.ToString(), response.LaunchId);
        Assert.Equal("", (await SkuAsync(t, skus[1].Id.ToString())).LaunchId);
        foreach (var id in new[] { Guid.NewGuid().ToString(), "x" })
            await ExpectNotFoundAsync(() => SkuAsync(t, id));
    }

    [Fact]
    public async Task C01_change_serialized_after_LockSkuForStock_is_SERIALIZED_LOCKED()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, skus) = await Seed.ProductAsync(t, category.Id);
        var first = await LockAsync(t, skus[0].Id.ToString());
        var again = await LockAsync(t, skus[0].Id.ToString());

        Assert.Equal(product.Id.ToString(), first.ProductId);
        Assert.False(first.Serialized);
        Assert.Equal(first, again);
        await using (var db = t.NewDb())
            Assert.True((await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id, TestContext.Current.CancellationToken)).SerializedLocked);

        var body = new
        {
            categoryId = category.Id, name = product.Name, slug = product.Slug, descriptionMd = "", specs = Array.Empty<object>(),
            serialized = true, coverageMonths = 0, version = "1",
        };
        await (await t.SendAsync(HttpMethod.Put, $"/api/admin/catalog/products/{product.Id}", body, Manager)).ExpectErrorAsync(400, "SERIALIZED_LOCKED");
    }

    [Fact]
    public async Task LockSkuForStock_unknown_sku_is_NOT_FOUND()
    {
        await using var t = await TestCatalog.StartAsync(containers);

        foreach (var id in new[] { Guid.NewGuid().ToString(), "x" })
            await ExpectNotFoundAsync(() => LockAsync(t, id));
    }

    [Fact]
    public async Task Internal_methods_have_no_http_route()
    {
        await using var t = await TestCatalog.StartAsync(containers);

        var post = await t.SendAsync(HttpMethod.Post, "/bloom.catalog.v1.CatalogInternal/GetPricing", new { skuIds = new[] { Guid.NewGuid() } });
        var get = await t.SendAsync(HttpMethod.Get, "/api/catalog/pricing");

        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}
