using System.Text.Json;
using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Bloom.Catalog.Tests;

public class SearchTests(Containers containers)
{
    private const string Route = "/api/catalog/products";
    private static readonly TestCaller Manager = TestCaller.Staff("catalog_manager");

    private static Task<HttpResponseMessage> SearchAsync(TestCatalog t, string query = "") =>
        t.SendAsync(HttpMethod.Get, query == "" ? Route : $"{Route}?{query}");

    private static async Task<JsonElement> OkAsync(TestCatalog t, string query = "") => await (await SearchAsync(t, query)).OkJsonAsync();

    /// <summary>Card names in result order (an empty page has no <c>items</c> in proto3 JSON).</summary>
    private static string[] Names(JsonElement result) =>
        result.TryGetProperty("items", out var items) ? [.. items.EnumerateArray().Select(i => i.GetProperty("name").GetString()!)] : [];

    private static async Task<string[]> NamesAsync(TestCatalog t, string query = "") => Names(await OkAsync(t, query));

    private static string Ids(params Guid[] ids) => string.Join('&', ids.Select(i => $"optionValueIds={i}"));

    private static async Task UpdateAsync(TestCatalog t, Guid id, Action<UpdateSettersBuilder<Product>> set)
    {
        await using var db = t.NewDb();
        await db.Products.Where(p => p.Id == id).ExecuteUpdateAsync(set, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A product with one option type <paramref name="storageType"/> and one SKU per entry: its storage value, optional colour (a second
    /// type "Color") and whether the SKU is active. Returns the option value ids by text.
    /// </summary>
    private static async Task<(Product Product, Dictionary<string, Guid> Values)> OptionProductAsync(
        TestCatalog t, Guid categoryId, string name, string storageType, params (string Storage, string Color, bool Active)[] combos)
    {
        var (product, _) = await Seed.ProductAsync(t, categoryId, name: name, skuPrices: []);
        var storage = new OptionType { Id = Guid.CreateVersion7(), ProductId = product.Id, Name = storageType, Position = 0 };
        var color = new OptionType { Id = Guid.CreateVersion7(), ProductId = product.Id, Name = "Color", Position = 1 };
        var values = new Dictionary<string, Guid>();
        var rows = new List<OptionValue>();
        void Add(OptionType type, string text)
        {
            if (values.ContainsKey(text)) return;
            values[text] = Guid.CreateVersion7();
            rows.Add(new OptionValue { Id = values[text], OptionTypeId = type.Id, Value = text, Position = rows.Count });
        }
        foreach (var c in combos) { Add(storage, c.Storage); Add(color, c.Color); }

        await using var db = t.NewDb();
        db.OptionTypes.AddRange(storage, color);
        db.OptionValues.AddRange(rows);
        foreach (var c in combos)
        {
            var sku = new Sku
            {
                Id = Guid.CreateVersion7(), ProductId = product.Id, Code = $"OP-{Guid.NewGuid():N}".ToUpperInvariant(), PriceCents = 1_000,
                Active = c.Active, OptionKey = $"{values[c.Storage]},{values[c.Color]}",
            };
            db.Skus.Add(sku);
            db.SkuOptionValues.AddRange(new SkuOptionValue { SkuId = sku.Id, OptionValueId = values[c.Storage] },
                new SkuOptionValue { SkuId = sku.Id, OptionValueId = values[c.Color] });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new ProductDerived(db).RefreshAsync(product.Id);
        return (product, values);
    }

    [Fact]
    public async Task C12_pro_max_256_with_storage_filter_matches_only_those()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var proMax = await Seed.PhoneAsync(t, category.Id, "Bloom Phone Pro Max");
        var (_, pro) = await OptionProductAsync(t, category.Id, "Bloom Phone Pro", "Storage", ("128 GB", "Black", true));
        await Seed.ProductAsync(t, category.Id, name: "Bloom Tablet");

        Assert.Equal(["Bloom Phone Pro Max"], await NamesAsync(t, "q=pro+max+256"));
        Assert.Equal(["Bloom Phone Pro Max"], await NamesAsync(t, $"q=pro+max+256&{Ids(proMax.Storage256)}"));
        Assert.Equal(["Bloom Phone Pro Max", "Bloom Phone Pro"], await NamesAsync(t, "q=pro&sort=relevance")); // title match; Pro Max is newer
        Assert.Empty(await NamesAsync(t, $"q=tablet&{Ids(proMax.Storage256)}"));
    }

    [Fact]
    public async Task Option_filter_is_or_within_type_and_on_one_sku()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id, "Phone");
        var (_, split) = await OptionProductAsync(t, category.Id, "Split", "Storage", ("512 GB", "White", true), ("128 GB", "Black", true));
        var (_, inactive) = await OptionProductAsync(t, category.Id, "Inactive", "Storage", ("256 GB", "Black", false), ("128 GB", "White", true));
        var (_, other) = await OptionProductAsync(t, category.Id, "Other", "storage", ("512 GB", "Black", true));

        // Storage {256, 512} + Color {Black}: Phone (256/Black), not Split (the 512 SKU is White, the Black one is 128), not Inactive.
        Assert.Equal(["Phone"], await NamesAsync(t, Ids(phone.Storage256, phone.Storage512, phone.Black)));
        // Values of two products' "Storage" / "Color" types (any case) group together by type name (owner item 4).
        Assert.Equal(["Other", "Phone"], (await NamesAsync(t, Ids(phone.Storage256, other["512 GB"], phone.Black, other["Black"]))).Order());
        // Same type is ORed: 256 (Phone) or 512 (Other).
        Assert.Equal(["Other", "Phone"], (await NamesAsync(t, Ids(phone.Storage256, other["512 GB"]))).Order());
        // Different types are ANDed on one SKU.
        Assert.Equal(["Split"], await NamesAsync(t, Ids(split["512 GB"], split["White"])));
        Assert.Empty(await NamesAsync(t, Ids(split["512 GB"], split["Black"])));
        Assert.Empty(await NamesAsync(t, Ids(inactive["256 GB"]))); // inactive SKUs don't count
        // Unknown ids match nothing; malformed ones are rejected.
        Assert.Empty(await NamesAsync(t, Ids(Guid.NewGuid())));
        Assert.Empty(await NamesAsync(t, Ids(phone.Storage256, Guid.NewGuid())));
        var info = await (await SearchAsync(t, "optionValueIds=nope")).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("option_value_ids", info.GetProperty("metadata").GetProperty("field").GetString());
    }

    [Fact]
    public async Task Category_filter_includes_sub_categories()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var top = await Seed.CategoryAsync(t);
        var sub = await Seed.CategoryAsync(t, top.Id);
        var other = await Seed.CategoryAsync(t);
        await Seed.ProductAsync(t, top.Id, name: "InTop");
        await Seed.ProductAsync(t, sub.Id, name: "InSub");
        await Seed.ProductAsync(t, other.Id, name: "InOther");

        Assert.Equal(["InSub", "InTop"], (await NamesAsync(t, $"categoryId={top.Id}")).Order());
        Assert.Equal(["InSub"], await NamesAsync(t, $"categoryId={sub.Id}"));
        Assert.Equal(["InOther"], await NamesAsync(t, $"categoryId={other.Id}"));
        Assert.Empty(await NamesAsync(t, $"categoryId={Guid.NewGuid()}"));
        await (await SearchAsync(t, "categoryId=nope")).ExpectErrorAsync(400, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Price_filter_and_sorts()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var cheap = (await Seed.ProductAsync(t, category.Id, name: "Cheap", skuPrices: [100, 5_000])).Product;
        var mid = (await Seed.ProductAsync(t, category.Id, name: "Mid", skuPrices: [200])).Product;
        var dear = (await Seed.ProductAsync(t, category.Id, name: "Dear", skuPrices: [300])).Product;
        var none = (await Seed.ProductAsync(t, category.Id, name: "NoPrice", skuPrices: [])).Product;
        var now = t.Clock.GetUtcNow();
        await UpdateAsync(t, cheap.Id, s => s.SetProperty(p => p.PublishedAt, now.AddDays(-3)));
        await UpdateAsync(t, mid.Id, s => s.SetProperty(p => p.PublishedAt, now.AddDays(-1)));
        await UpdateAsync(t, dear.Id, s => s.SetProperty(p => p.PublishedAt, now.AddDays(-2)));
        await UpdateAsync(t, none.Id, s => s.SetProperty(p => p.PublishedAt, now));

        Assert.Equal(["Cheap", "Mid", "Dear", "NoPrice"], await NamesAsync(t, "sort=price_asc"));
        Assert.Equal(["Dear", "Mid", "Cheap", "NoPrice"], await NamesAsync(t, "sort=price_desc"));
        Assert.Equal(["NoPrice", "Mid", "Dear", "Cheap"], await NamesAsync(t, "sort=newest"));
        Assert.Equal(["NoPrice", "Mid", "Dear", "Cheap"], await NamesAsync(t)); // relevance without text is newest
        Assert.Equal(["Mid", "Dear"], await NamesAsync(t, "priceMin=150&priceMax=300&sort=price_asc")); // on the minimum price; no-price products excluded
        Assert.Equal(["Cheap"], await NamesAsync(t, "priceMax=150"));
        Assert.Equal(["Dear"], await NamesAsync(t, "priceMin=300"));
        await (await SearchAsync(t, "sort=cheapest")).ExpectErrorAsync(400, "VALIDATION_FAILED");

        // rating: average desc, then count desc, unrated last, then id.
        var rated = new[] { ("A", 5, 1), ("B", 9, 2), ("C", 10, 2), ("D", 0, 0) };
        foreach (var (name, sum, count) in rated)
        {
            var id = (await Seed.ProductAsync(t, category.Id, name: name)).Product.Id;
            await UpdateAsync(t, id, s => s.SetProperty(p => p.RatingSum, sum).SetProperty(p => p.RatingCount, count));
        }
        var byRating = await NamesAsync(t, "sort=rating");
        Assert.Equal(["C", "A", "B"], byRating[..3]);
        Assert.Equal(["Cheap", "D", "Dear", "Mid", "NoPrice"], byRating[3..].Order()); // unrated last
    }

    [Fact]
    public async Task Stop_word_query_returns_filtered_results()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var elsewhere = await Seed.CategoryAsync(t);
        var old = (await Seed.ProductAsync(t, category.Id, name: "Old")).Product;
        await Seed.ProductAsync(t, category.Id, name: "New");
        await Seed.ProductAsync(t, elsewhere.Id, name: "Elsewhere");
        await UpdateAsync(t, old.Id, s => s.SetProperty(p => p.PublishedAt, t.Clock.GetUtcNow().AddDays(-1)));

        Assert.Equal(["New", "Old"], await NamesAsync(t, $"q=the+and&categoryId={category.Id}"));
        Assert.Equal(["New", "Old"], await NamesAsync(t, $"q=the+and&categoryId={category.Id}&sort=relevance")); // newest first
    }

    [Fact]
    public async Task Only_visible_physical_products_are_found()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        await Seed.ProductAsync(t, category.Id, name: "Widget Visible");
        await Seed.ProductAsync(t, category.Id, "draft", name: "Widget Draft");
        await Seed.ProductAsync(t, category.Id, "archived", name: "Widget Archived");
        var launchDraft = (await Seed.ProductAsync(t, category.Id, name: "Widget LaunchDraft")).Product;
        var announced = (await Seed.ProductAsync(t, category.Id, "draft", name: "Widget Announced")).Product;
        var preorder = (await Seed.ProductAsync(t, category.Id, "archived", name: "Widget Preorder")).Product;
        var releasedDraft = (await Seed.ProductAsync(t, category.Id, "draft", name: "Widget ReleasedDraft")).Product;
        var (plan, _) = await Seed.PlanAsync(t, category.Id, []);
        await UpdateAsync(t, plan.Id, s => s.SetProperty(p => p.Name, "Widget Plan"));
        await using (var db = t.NewDb()) await new ProductDerived(db).RefreshAsync(plan.Id);
        await UpdateAsync(t, launchDraft.Id, s => s.SetProperty(p => p.LaunchStage, LaunchStages.Draft));
        await UpdateAsync(t, announced.Id, s => s.SetProperty(p => p.LaunchStage, LaunchStages.Announced));
        await UpdateAsync(t, preorder.Id, s => s.SetProperty(p => p.LaunchStage, LaunchStages.PreorderOpen));
        await UpdateAsync(t, releasedDraft.Id, s => s.SetProperty(p => p.LaunchStage, LaunchStages.Released));

        var found = await OkAsync(t, "q=widget");
        Assert.Equal(["Widget Announced", "Widget Preorder", "Widget Visible"], Names(found).Order());
        Assert.Equal(3, found.GetProperty("total").GetInt32());
        Assert.Equal(3, Names(await OkAsync(t)).Length); // and without a text filter
    }

    [Fact]
    public async Task Public_product_sql_agrees_with_the_csharp_predicate()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        string?[] stages = [null, .. LaunchStages.All];
        var rows = (from kind in ProductKinds.All from status in ProductStatuses.All from stage in stages select (kind, status, stage))
            .Select(x =>
            {
                var id = Guid.CreateVersion7();
                return new Product
                {
                    Id = id, CategoryId = category.Id, Kind = x.kind, Status = x.status, LaunchStage = x.stage, Name = $"P {id:N}", Slug = $"s-{id:N}",
                    UpdatedAt = t.Clock.GetUtcNow(),
                };
            }).ToList();
        await using var db = t.NewDb();
        db.Products.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        const string sql = $"SELECT p.id AS \"Value\" FROM products p WHERE {Visibility.PublicProductSql}";
        var bySql = await db.Database.SqlQueryRaw<Guid>(sql).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(rows.Where(Visibility.IsPublic).Select(r => r.Id).Order(), bySql.Order());
        Assert.InRange(bySql.Count, 1, rows.Count - 1);
    }

    [Fact]
    public async Task Paging_rules()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        for (var i = 0; i < 25; i++) await Seed.ProductAsync(t, category.Id);

        var info = await (await SearchAsync(t, "pageSize=49")).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("page_size", info.GetProperty("metadata").GetProperty("field").GetString());
        await (await SearchAsync(t, "page=-1")).ExpectErrorAsync(400, "VALIDATION_FAILED");

        var first = await OkAsync(t, "pageSize=0");
        Assert.Equal(24, Names(first).Length);
        Assert.Equal(25, first.GetProperty("total").GetInt32());
        Assert.Equal(24, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, first.GetProperty("page").GetInt32());

        var second = await OkAsync(t, "page=2");
        Assert.Single(Names(second));
        var beyond = await OkAsync(t, "page=99&pageSize=10");
        Assert.Empty(Names(beyond));
        Assert.Equal(25, beyond.GetProperty("total").GetInt32());
        Assert.Equal(99, beyond.GetProperty("page").GetInt32());
        Assert.Equal(25, Names(await OkAsync(t, "pageSize=48")).Length);
    }

    [Fact]
    public async Task Result_page_is_cached_by_catalog_version()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var (product, skus) = await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id, name: "Cached");
        using var metrics = new MetricCapture(t.Services);

        var first = await OkAsync(t, "q=cached");
        var second = await OkAsync(t, "q=cached");
        Assert.Equal(first.ToString(), second.ToString());
        Assert.Equal(1, metrics.Count("catalog_read_seconds", ("endpoint", "search_products"), ("cache", "miss")));
        Assert.Equal(1, metrics.Count("catalog_read_seconds", ("endpoint", "search_products"), ("cache", "hit")));
        Assert.Equal(1, metrics.Count("search_seconds"));

        await (await t.SendAsync(HttpMethod.Put, $"/api/admin/catalog/skus/{skus[0].Id}/price", new { priceCents = 89_900, version = "1" }, Manager)).OkJsonAsync();
        Assert.Equal(1, (long)await t.Redis.StringGetAsync("catalog:version"));

        var after = await OkAsync(t, "q=cached");
        Assert.Equal("89900", after.GetProperty("items")[0].GetProperty("minPriceCents").GetString());
        Assert.Equal(2, metrics.Count("catalog_read_seconds", ("endpoint", "search_products"), ("cache", "miss")));
        Assert.Equal(2, metrics.Count("search_seconds"));
        Assert.Equal(product.Id.ToString(), after.GetProperty("items")[0].GetProperty("id").GetString());
        await OkAsync(t, "q=cached");
        Assert.Equal(2, metrics.Count("search_seconds"));
    }

    [Fact]
    public async Task Redis_down_search_still_served()
    {
        await using var t = await TestCatalog.StartAsync(containers, o => o.Config["Catalog:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200");
        await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id);

        for (var i = 0; i < 2; i++) Assert.Equal(["Bloom Phone Pro Max"], await NamesAsync(t, "q=phone"));
    }

    [Fact]
    public async Task Card_shows_first_ready_image()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id, name: "Pictured");
        await Seed.ProductAsync(t, category.Id, name: "Bare");
        await UpdateAsync(t, product.Id, s => s.SetProperty(p => p.RatingSum, 9).SetProperty(p => p.RatingCount, 2)
            .SetProperty(p => p.LaunchId, Guid.NewGuid()).SetProperty(p => p.LaunchStage, LaunchStages.Released));
        await using (var db = t.NewDb())
        {
            ProductImage Image(string file, int position, string status) => new()
            {
                Id = Guid.CreateVersion7(), ProductId = product.Id, StorageKey = $"products/{product.Id}/{file}", Position = position, Status = status,
                CreatedAt = t.Clock.GetUtcNow(),
            };
            db.ProductImages.AddRange(Image("pending.jpg", 0, ImageStatuses.Pending), Image("second.jpg", 2, ImageStatuses.Ready), Image("first.jpg", 1, ImageStatuses.Ready));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var items = (await OkAsync(t)).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!);

        var card = items["Pictured"];
        Assert.Equal($"{containers.MinioUrl}/{t.Bucket}/products/{product.Id}/first.jpg", card.GetProperty("imageUrl").GetString());
        Assert.Equal(product.Id.ToString(), card.GetProperty("id").GetString());
        Assert.Equal(product.Slug, card.GetProperty("slug").GetString());
        Assert.Equal("99900", card.GetProperty("minPriceCents").GetString());
        Assert.Equal(4.5, card.GetProperty("ratingAverage").GetDouble());
        Assert.Equal(2, card.GetProperty("ratingCount").GetInt32());
        Assert.Equal("released", card.GetProperty("launchStage").GetString());
        Assert.True(!items["Bare"].TryGetProperty("imageUrl", out var none) || none.GetString() == "");
    }
}
