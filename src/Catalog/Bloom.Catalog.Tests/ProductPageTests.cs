using System.Text.Json;
using Bloom.Catalog.Caching;
using Bloom.Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Bloom.Catalog.Tests;

public class ProductPageTests(Containers containers)
{
    private const string Page = "/api/catalog/products";
    private static readonly TestCaller Manager = TestCaller.Staff("catalog_manager");

    private static Task<HttpResponseMessage> GetAsync(TestCatalog t, Guid id) => t.SendAsync(HttpMethod.Get, $"{Page}/{id}");

    private static Task<HttpResponseMessage> GetBySlugAsync(TestCatalog t, string slug) => t.SendAsync(HttpMethod.Get, $"{Page}/by-slug/{slug}");

    private static async Task UpdateAsync(TestCatalog t, Guid id, Action<UpdateSettersBuilder<Product>> set)
    {
        await using var db = t.NewDb();
        await db.Products.Where(p => p.Id == id).ExecuteUpdateAsync(set, TestContext.Current.CancellationToken);
    }

    private static async Task AddImageAsync(TestCatalog t, Guid productId, string file, int position, string status, Guid? skuId = null)
    {
        await using var db = t.NewDb();
        db.ProductImages.Add(new ProductImage
        {
            Id = Guid.CreateVersion7(), ProductId = productId, StorageKey = $"products/{productId}/{file}", AltText = file, Position = position,
            Status = status, SkuId = skuId, CreatedAt = t.Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Product_page_shows_public_parts_only()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var id = phone.Product.Id;
        await UpdateAsync(t, id, s => s
            .SetProperty(p => p.DescriptionMd, "**secret** md").SetProperty(p => p.DescriptionHtml, "<p><strong>bold</strong></p>")
            .SetProperty(p => p.Specs, """[{"group":"Display","items":[{"label":"Size","value":"6.9 in"}]}]""")
            .SetProperty(p => p.RatingSum, 9).SetProperty(p => p.RatingCount, 2));
        await using (var db = t.NewDb())
            await db.Skus.Where(s => s.Id == phone.Skus[3].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), TestContext.Current.CancellationToken);
        await AddImageAsync(t, id, "pending.jpg", 0, ImageStatuses.Pending);
        await AddImageAsync(t, id, "two.jpg", 2, ImageStatuses.Ready);
        await AddImageAsync(t, id, "one.jpg", 1, ImageStatuses.Ready);
        await AddImageAsync(t, id, "inactive-sku.jpg", 3, ImageStatuses.Ready, phone.Skus[3].Id); // linked to the inactive SKU: not public

        var view = await (await GetAsync(t, id)).OkJsonAsync();

        Assert.Equal(3, view.GetProperty("skus").GetArrayLength());
        Assert.DoesNotContain(phone.Skus[3].Id.ToString(), view.GetProperty("skus").ToString());
        var sku = view.GetProperty("skus").EnumerateArray().Single(s => s.GetProperty("id").GetString() == phone.Skus[0].Id.ToString());
        Assert.Equal(phone.Skus[0].Code, sku.GetProperty("code").GetString());
        Assert.Equal("99900", sku.GetProperty("priceCents").GetString());
        Assert.Equal([phone.Storage256.ToString(), phone.Black.ToString()], sku.GetProperty("optionValueIds").EnumerateArray().Select(v => v.GetString()));
        var images = view.GetProperty("images").EnumerateArray().ToList();
        Assert.Equal([$"{containers.MinioUrl}/{t.Bucket}/products/{id}/one.jpg", $"{containers.MinioUrl}/{t.Bucket}/products/{id}/two.jpg"],
            images.Select(i => i.GetProperty("url").GetString()));
        Assert.All(images, i => Assert.True(!i.TryGetProperty("status", out var status) || status.GetString() == ""));
        Assert.Equal("<p><strong>bold</strong></p>", view.GetProperty("descriptionHtml").GetString());
        Assert.False(view.TryGetProperty("descriptionMd", out _));
        Assert.False(view.TryGetProperty("serializedLocked", out _));
        Assert.Equal("Display", view.GetProperty("specs")[0].GetProperty("group").GetString());
        Assert.Equal(["Storage", "Color"], view.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("name").GetString()));
        Assert.Equal(category.Id.ToString(), view.GetProperty("categoryId").GetString());
        Assert.Equal("99900", view.GetProperty("minPriceCents").GetString());
        Assert.Equal(4.5, view.GetProperty("ratingAverage").GetDouble());
        Assert.Equal(2, view.GetProperty("ratingCount").GetInt32());
    }

    [Fact]
    public async Task By_slug_returns_the_same_view()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id);

        var byId = await (await GetAsync(t, phone.Product.Id)).OkJsonAsync();
        var bySlug = await (await GetBySlugAsync(t, phone.Product.Slug)).OkJsonAsync();

        Assert.Equal(byId.ToString(), bySlug.ToString());
        await (await GetBySlugAsync(t, "no-such-slug")).ExpectErrorAsync(404, "NOT_FOUND");
    }

    [Fact]
    public async Task C04_price_change_then_page_read_shows_new_price()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, skus) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var key = CatalogCache.ProductKey(product.Id);

        var before = await (await GetAsync(t, product.Id)).OkJsonAsync();
        Assert.Equal("99900", before.GetProperty("skus")[0].GetProperty("priceCents").GetString());
        Assert.True(await t.Redis.KeyExistsAsync(key));

        await (await t.SendAsync(HttpMethod.Put, $"/api/admin/catalog/skus/{skus[0].Id}/price", new { priceCents = 89_900, version = "1" }, Manager)).OkJsonAsync();
        Assert.False(await t.Redis.KeyExistsAsync(key));

        var after = await (await GetAsync(t, product.Id)).OkJsonAsync();
        Assert.Equal("89900", after.GetProperty("skus")[0].GetProperty("priceCents").GetString());
        Assert.Equal("89900", after.GetProperty("minPriceCents").GetString());
    }

    [Theory, InlineData("draft"), InlineData("archived")]
    public async Task Hidden_product_is_NOT_FOUND(string status)
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id, status);

        await (await GetAsync(t, product.Id)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await GetBySlugAsync(t, product.Slug)).ExpectErrorAsync(404, "NOT_FOUND");
        Assert.False(await t.Redis.KeyExistsAsync(CatalogCache.ProductKey(product.Id)));
    }

    [Fact]
    public async Task Plan_has_no_public_page()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var (plan, _) = await Seed.PlanAsync(t, category.Id, [phone.Product.Id]);

        await (await GetAsync(t, plan.Id)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await GetBySlugAsync(t, plan.Slug)).ExpectErrorAsync(404, "NOT_FOUND");
        Assert.False(await t.Redis.KeyExistsAsync(CatalogCache.ProductKey(plan.Id)));
    }

    [Fact]
    public async Task Device_page_lists_shown_warranty_plans()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var (shown, shownSku) = await Seed.PlanAsync(t, category.Id, [phone.Product.Id], price: 12_900, coverageMonths: 24);
        await Seed.PlanAsync(t, category.Id, [phone.Product.Id], status: "archived");
        var (_, inactiveSku) = await Seed.PlanAsync(t, category.Id, [phone.Product.Id]);
        await using (var db = t.NewDb())
            await db.Skus.Where(s => s.Id == inactiveSku.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Active, false), TestContext.Current.CancellationToken);

        var plans = (await (await GetAsync(t, phone.Product.Id)).OkJsonAsync()).GetProperty("warrantyPlans");

        var plan = Assert.Single(plans.EnumerateArray());
        Assert.Equal(shown.Id.ToString(), plan.GetProperty("productId").GetString());
        Assert.Equal(shown.Name, plan.GetProperty("name").GetString());
        Assert.Equal(24, plan.GetProperty("coverageMonths").GetInt32());
        Assert.Equal(shownSku.Id.ToString(), plan.GetProperty("skuId").GetString());
        Assert.Equal("12900", plan.GetProperty("priceCents").GetString());
    }

    [Fact]
    public async Task Last_active_sku_deactivated_keeps_page_visible()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, skus) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        await (await GetAsync(t, product.Id)).OkJsonAsync(); // cached

        await (await t.SendAsync(HttpMethod.Put, $"/api/admin/catalog/skus/{skus[0].Id}/active", new { active = false, version = "1" }, Manager)).OkJsonAsync();

        var view = await (await GetAsync(t, product.Id)).OkJsonAsync();
        Assert.True(!view.TryGetProperty("skus", out var list) || list.GetArrayLength() == 0);
        Assert.False(view.TryGetProperty("minPriceCents", out _));
    }

    [Fact]
    public async Task Launch_stage_controls_visibility()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        var launchId = Guid.NewGuid();

        await UpdateAsync(t, product.Id, s => s.SetProperty(p => p.LaunchId, launchId).SetProperty(p => p.LaunchStage, LaunchStages.Draft));
        await (await GetAsync(t, product.Id)).ExpectErrorAsync(404, "NOT_FOUND"); // active, but its launch is still a draft

        await UpdateAsync(t, product.Id, s => s.SetProperty(p => p.LaunchStage, LaunchStages.Announced).SetProperty(p => p.Status, ProductStatuses.Draft));
        var view = await (await GetAsync(t, product.Id)).OkJsonAsync();
        Assert.Equal(launchId.ToString(), view.GetProperty("launch").GetProperty("launchId").GetString());
        Assert.Equal("announced", view.GetProperty("launch").GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Cache_hit_and_miss_are_measured()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, _) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id);
        using var metrics = new MetricCapture(t.Services);

        await (await GetAsync(t, product.Id)).OkJsonAsync();
        await (await GetAsync(t, product.Id)).OkJsonAsync();

        Assert.Equal(1, metrics.Count("catalog_read_seconds", ("endpoint", "get_product"), ("cache", "miss")));
        Assert.Equal(1, metrics.Count("catalog_read_seconds", ("endpoint", "get_product"), ("cache", "hit")));
        Assert.Equal(0.5, metrics.Latest("catalog_cache_hit_ratio", ("kind", "product")));
    }

    [Fact]
    public async Task Redis_down_page_still_served()
    {
        await using var t = await TestCatalog.StartAsync(containers, o => o.Config["Catalog:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200");
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id);

        for (var i = 0; i < 2; i++)
        {
            var view = await (await GetAsync(t, phone.Product.Id)).OkJsonAsync();
            Assert.Equal(4, view.GetProperty("skus").GetArrayLength());
        }
    }
}
