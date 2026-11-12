using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Tests;

public class LifecycleTests(Containers containers)
{
    private const string Admin = "/api/admin/catalog";
    private static readonly TestCaller Manager = TestCaller.Staff("catalog_manager");

    private static Task<HttpResponseMessage> ActAsync(TestCatalog t, Guid id, string action, object version) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/products/{id}/{action}", new { version }, Manager);

    [Fact]
    public async Task Publish_needs_an_active_sku()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (none, _) = await Seed.ProductAsync(t, category.Id, "draft", skuPrices: []);
        var (inactive, skus) = await Seed.ProductAsync(t, category.Id, "draft");
        await using (var db = t.NewDb())
            await db.Skus.Where(s => s.Id == skus[0].Id).ExecuteUpdateAsync(b => b.SetProperty(s => s.Active, false), TestContext.Current.CancellationToken);
        var (ready, _) = await Seed.ProductAsync(t, category.Id, "draft");

        await (await ActAsync(t, none.Id, "publish", "1")).ExpectErrorAsync(400, "PUBLISH_INCOMPLETE");
        await (await ActAsync(t, inactive.Id, "publish", "1")).ExpectErrorAsync(400, "PUBLISH_INCOMPLETE");
        Assert.Empty(await Outbox.AuditAsync(t));

        t.Clock.Advance(TimeSpan.FromMinutes(5));
        var published = await (await ActAsync(t, ready.Id, "publish", "1")).OkJsonAsync();

        Assert.Equal("active", published.GetProperty("status").GetString());
        Assert.Equal("2", published.GetProperty("version").GetString());
        Assert.True((t.Clock.GetUtcNow() - published.GetProperty("publishedAt").GetDateTimeOffset()).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Archive_and_reactivate()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id);
        var publishedAt = t.Clock.GetUtcNow();
        await t.Redis.StringSetAsync($"product:{product.Id}", "x");
        await t.Redis.StringSetAsync("catalog:version", "7");
        t.Clock.Advance(TimeSpan.FromHours(1));

        var archived = await (await ActAsync(t, product.Id, "archive", "1")).OkJsonAsync();
        Assert.Equal("archived", archived.GetProperty("status").GetString());
        Assert.Equal("2", archived.GetProperty("version").GetString());
        Assert.False(await t.Redis.KeyExistsAsync($"product:{product.Id}"));
        Assert.Equal(8, (long)await t.Redis.StringGetAsync("catalog:version"));

        var back = await (await ActAsync(t, product.Id, "reactivate", "2")).OkJsonAsync();
        Assert.Equal("active", back.GetProperty("status").GetString());
        Assert.Equal("3", back.GetProperty("version").GetString());
        Assert.True((publishedAt - back.GetProperty("publishedAt").GetDateTimeOffset()).Duration() < TimeSpan.FromMilliseconds(1)); // unchanged

        await (await ActAsync(t, product.Id, "archive", "1")).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Plan_lifecycle_deletes_eligible_device_pages_and_bumps_the_listings_version()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var device = await Seed.PhoneAsync(t, category.Id);
        var unrelated = await Seed.PhoneAsync(t, category.Id, "Unrelated");
        var (plan, _) = await Seed.PlanAsync(t, category.Id, [device.Product.Id], status: "draft");
        var version = 1L;
        async Task StepAsync(string action, long planVersion)
        {
            await t.Redis.StringSetAsync($"product:{device.Product.Id}", "x");
            await t.Redis.StringSetAsync($"product:{unrelated.Product.Id}", "x");
            await t.Redis.StringSetAsync("catalog:version", version);

            await (await ActAsync(t, plan.Id, action, planVersion)).OkJsonAsync();

            Assert.False(await t.Redis.KeyExistsAsync($"product:{device.Product.Id}"), action);
            Assert.True(await t.Redis.KeyExistsAsync($"product:{unrelated.Product.Id}"), action);
            Assert.Equal(version + 1, (long)await t.Redis.StringGetAsync("catalog:version"));
            version++;
        }

        await StepAsync("publish", 1);
        await StepAsync("archive", 2);
        await StepAsync("reactivate", 3);
    }

    [Theory]
    [InlineData("draft", "archive")]
    [InlineData("draft", "reactivate")]
    [InlineData("active", "publish")]
    [InlineData("active", "reactivate")]
    [InlineData("archived", "publish")]
    [InlineData("archived", "archive")]
    public async Task Disallowed_transition_is_INVALID_LIFECYCLE(string from, string action)
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id, from);

        await (await ActAsync(t, product.Id, action, "1")).ExpectErrorAsync(400, "INVALID_LIFECYCLE");

        await using var db = t.NewDb();
        var row = await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id, TestContext.Current.CancellationToken);
        Assert.Equal((from, 1L), (row.Status, row.Version));
        Assert.Empty(await Outbox.AuditAsync(t));
    }

    [Fact]
    public async Task Lifecycle_is_audited_with_status_before_after()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id, "draft");

        await (await ActAsync(t, product.Id, "publish", "1")).OkJsonAsync();
        await (await ActAsync(t, product.Id, "archive", "2")).OkJsonAsync();
        await (await ActAsync(t, product.Id, "reactivate", "3")).OkJsonAsync();

        var entries = await Outbox.AuditAsync(t);
        Assert.Equal(["catalog.product-published", "catalog.product-archived", "catalog.product-reactivated"], entries.Select(e => e.Action));
        string[][] statuses = [["draft", "active"], ["active", "archived"], ["archived", "active"]];
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(("product", product.Id.ToString("D"), "catalog_manager"), (entries[i].TargetType, entries[i].TargetId, entries[i].ActorRole));
            Assert.Equal(statuses[i][0], entries[i].Details.GetProperty("before").GetProperty("status").GetString());
            Assert.Equal(statuses[i][1], entries[i].Details.GetProperty("after").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task No_delete_route_exists()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id);

        var response = await t.SendAsync(HttpMethod.Delete, $"{Admin}/products/{product.Id}", caller: Manager);

        Assert.Equal(405, (int)response.StatusCode); // the path exists for GET and PUT, so routing answers 405 rather than 404; no DELETE handler either way
        await using var db = t.NewDb();
        Assert.True(await db.Products.AnyAsync(p => p.Id == product.Id, TestContext.Current.CancellationToken)); // CR-06
    }
}
