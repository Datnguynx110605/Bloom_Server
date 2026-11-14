using System.Net;
using System.Text.Json;
using Bloom.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Tests;

public class ProductAdminTests(Containers containers)
{
    private const string Admin = "/api/admin/catalog/products";
    private static readonly TestCaller Manager = TestCaller.Staff("catalog_manager");

    private static object Body(Guid categoryId, string name = "Bloom Phone", string slug = "bloom-phone", string kind = "physical",
        string md = "", bool serialized = false, int coverage = 0, object[]? specs = null) =>
        new { categoryId, kind, name, slug, descriptionMd = md, specs = specs ?? [], serialized, coverageMonths = coverage };

    private static object UpdateBody(Guid categoryId, string name, string slug, string version, string md = "", bool serialized = false,
        int coverage = 0, object[]? specs = null) =>
        new { categoryId, name, slug, descriptionMd = md, specs = specs ?? [], serialized, coverageMonths = coverage, version };

    private static Task<HttpResponseMessage> CreateAsync(TestCatalog t, object body, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, Admin, body, caller ?? Manager);

    private static Task<HttpResponseMessage> UpdateAsync(TestCatalog t, string id, object body, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Put, $"{Admin}/{id}", body, caller ?? Manager);

    private static async Task<string> FieldAsync(HttpResponseMessage response, int status = 400)
    {
        var info = await response.ExpectErrorAsync(status, "VALIDATION_FAILED");
        return info.GetProperty("metadata").GetProperty("field").GetString()!;
    }

    private static async Task<bool> VectorMatchesAsync(TestCatalog t, string id, string word)
    {
        await using var db = t.NewDb();
        return await db.Database.SqlQuery<bool>($"SELECT search_vector @@ to_tsquery('english', {word}) AS \"Value\" FROM products WHERE id = {Guid.Parse(id)}")
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Create_product_starts_as_draft_with_rendered_description()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);

        var specs = new object[] { new { group = "Display", items = new[] { new { label = "Size", value = "6.1 in" } } } };
        var created = await (await CreateAsync(t, Body(category.Id, md: "**x**", specs: specs))).OkJsonAsync();

        Assert.Equal("draft", created.GetProperty("status").GetString());
        Assert.Equal("1", created.GetProperty("version").GetString());
        Assert.Equal("<p><strong>x</strong></p>\n", created.GetProperty("descriptionHtml").GetString());
        Assert.Equal("**x**", created.GetProperty("descriptionMd").GetString());
        Assert.Equal("physical", created.GetProperty("kind").GetString());
        Assert.Equal(category.Id.ToString(), created.GetProperty("categoryId").GetString());
        Assert.Equal("Size", created.GetProperty("specs")[0].GetProperty("items")[0].GetProperty("label").GetString());
        Assert.False(created.GetProperty("serializedLocked").GetBoolean());
        Assert.False(created.TryGetProperty("minPriceCents", out _));

        var entry = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal("catalog.product-created", entry.Action);
        Assert.Equal("product", entry.TargetType);
        Assert.Equal(created.GetProperty("id").GetString(), entry.TargetId);
        Assert.Equal("catalog", entry.Service);
        Assert.Empty(entry.Details.EnumerateObject());
    }

    [Fact]
    public async Task Unsafe_markdown_never_reaches_html()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);

        var md = "<script>alert(1)</script>\n\n<img src=x onerror=y>\n\n[a](javascript:alert(1))\n\n![i](data:text/html,x)\n\n" +
                 "[b](JaVaScRiPt:alert(1))\n\n[c](<java&#9;script:alert(1)>)\n\n" +
                 "<javascript:alert(1)>\n\n<JAVASCRIPT:x>\n\n<data:text/html,x>\n\n<vbscript:x>\n\n[x][r]\n\n![y][r]\n\n[r]: javascript:y\n\n" +
                 "![z][d]\n\n[d]: data:text/html,x\n\n[x]( javascript:y)\n\n" +
                 "[ok](https://example.com/a:b) [rel](/x) [m](mailto:a@b.c) <https://ok.com> <a@b.c>";
        var created = await (await CreateAsync(t, Body(category.Id, md: md))).OkJsonAsync();

        var html = created.GetProperty("descriptionHtml").GetString()!;
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", html);
        Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<a href=\"https://ok.com\">https://ok.com</a>", html);
        Assert.Contains("<a href=\"mailto:a@b.c\">a@b.c</a>", html);
        Assert.Contains("href=\"https://example.com/a:b\"", html);
        Assert.Contains("href=\"/x\"", html);
        Assert.Contains("href=\"mailto:a@b.c\"", html);
    }

    [Fact]
    public async Task Update_changes_fields_and_audits_before_after()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id, status: "draft", name: "Old name");
        var before = await t.DbNowAsync();

        var updated = await (await UpdateAsync(t, product.Id.ToString(),
            UpdateBody(category.Id, "New name", product.Slug, "1", md: product.DescriptionMd))).OkJsonAsync();
        var after = await t.DbNowAsync();

        Assert.Equal("New name", updated.GetProperty("name").GetString());
        Assert.Equal("2", updated.GetProperty("version").GetString());
        Assert.Equal("draft", updated.GetProperty("status").GetString()); // lifecycle methods only
        Assert.InRange(updated.GetProperty("updatedAt").GetDateTimeOffset(), before, after); // the database clock
        var entry = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal("catalog.product-updated", entry.Action);
        Assert.Equal(["after", "before"], entry.Details.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Old name", entry.Details.GetProperty("before").GetProperty("name").GetString());
        Assert.Equal("New name", entry.Details.GetProperty("after").GetProperty("name").GetString());
        Assert.Single(entry.Details.GetProperty("before").EnumerateObject());
    }

    [Fact]
    public async Task Update_deletes_the_product_cache_entry_and_bumps_the_listings_version()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id);
        await t.Redis.StringSetAsync($"product:{product.Id}", "x");
        await t.Redis.StringSetAsync("catalog:version", "7");

        await (await UpdateAsync(t, product.Id.ToString(), UpdateBody(category.Id, "Renamed", product.Slug, "1"))).OkJsonAsync();

        Assert.False(await t.Redis.KeyExistsAsync($"product:{product.Id}"));
        Assert.Equal(8, (long)await t.Redis.StringGetAsync("catalog:version"));
    }

    [Fact]
    public async Task Changing_serialized_after_lock_is_SERIALIZED_LOCKED()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id, serializedLocked: true);

        await (await UpdateAsync(t, product.Id.ToString(), UpdateBody(category.Id, "Other name", product.Slug, "1", serialized: true)))
            .ExpectErrorAsync(400, "SERIALIZED_LOCKED");

        await using var db = t.NewDb();
        var row = await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id, TestContext.Current.CancellationToken);
        Assert.Equal(product.Name, row.Name);
        Assert.False(row.Serialized);
        Assert.Equal(1, row.Version);
        Assert.Empty(await Outbox.AuditAsync(t));

        // an unchanged flag is fine even when locked
        await (await UpdateAsync(t, product.Id.ToString(), UpdateBody(category.Id, "Other name", product.Slug, "1"))).OkJsonAsync();
    }

    [Fact]
    public async Task Serialized_changes_before_lock()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id);

        var updated = await (await UpdateAsync(t, product.Id.ToString(), UpdateBody(category.Id, product.Name, product.Slug, "1", serialized: true))).OkJsonAsync();

        Assert.True(updated.GetProperty("serialized").GetBoolean());
        Assert.Equal("2", updated.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Plan_and_physical_field_rules()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);

        Assert.Equal("serialized", await FieldAsync(await CreateAsync(t, Body(category.Id, kind: "warranty_plan", serialized: true, coverage: 12))));
        Assert.Equal("coverage_months", await FieldAsync(await CreateAsync(t, Body(category.Id, kind: "warranty_plan", coverage: 0))));
        Assert.Equal("coverage_months", await FieldAsync(await CreateAsync(t, Body(category.Id, kind: "warranty_plan", coverage: 121))));
        Assert.Equal("coverage_months", await FieldAsync(await CreateAsync(t, Body(category.Id, coverage: 12))));
        var plan = await (await CreateAsync(t, Body(category.Id, "Care", "care", kind: "warranty_plan", coverage: 120))).OkJsonAsync();
        Assert.Equal(120, plan.GetProperty("coverageMonths").GetInt32());

        // the kind of an existing product decides the rules
        var id = plan.GetProperty("id").GetString()!;
        Assert.Equal("serialized", await FieldAsync(await UpdateAsync(t, id, UpdateBody(category.Id, "Care", "care", "1", serialized: true, coverage: 12))));
        Assert.Equal("coverage_months", await FieldAsync(await UpdateAsync(t, id, UpdateBody(category.Id, "Care", "care", "1", coverage: 0))));
    }

    [Fact]
    public async Task Bad_fields_are_VALIDATION_FAILED()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var emptyLabel = new object[] { new { group = "G", items = new[] { new { label = " ", value = "v" } } } };
        var emptyValue = new object[] { new { group = "G", items = new[] { new { label = "l", value = "" } } } };
        var emptyGroup = new object[] { new { group = "", items = new[] { new { label = "l", value = "v" } } } };

        Assert.Equal("description_md", await FieldAsync(await CreateAsync(t, Body(category.Id, md: new string('a', 20_001)))));
        Assert.Equal("specs", await FieldAsync(await CreateAsync(t, Body(category.Id, specs: emptyLabel))));
        Assert.Equal("specs", await FieldAsync(await CreateAsync(t, Body(category.Id, specs: emptyValue))));
        Assert.Equal("specs", await FieldAsync(await CreateAsync(t, Body(category.Id, specs: emptyGroup))));
        Assert.Equal("kind", await FieldAsync(await CreateAsync(t, Body(category.Id, kind: "gadget"))));
        Assert.Equal("name", await FieldAsync(await CreateAsync(t, Body(category.Id, name: " "))));
        Assert.Equal("name", await FieldAsync(await CreateAsync(t, Body(category.Id, name: new string('n', 201)))));
        Assert.Equal("slug", await FieldAsync(await CreateAsync(t, Body(category.Id, slug: "Not Valid"))));
        await (await CreateAsync(t, Body(Guid.NewGuid()))).ExpectErrorAsync(404, "NOT_FOUND");

        // 20,000 scalars is fine, also when they are astral (two UTF-16 units each)
        await (await CreateAsync(t, Body(category.Id, md: string.Concat(Enumerable.Repeat("😀", 20_000))))).OkJsonAsync();

        var (product, _) = await Seed.ProductAsync(t, category.Id);
        Assert.Equal("description_md", await FieldAsync(await UpdateAsync(t, product.Id.ToString(),
            UpdateBody(category.Id, product.Name, product.Slug, "1", md: new string('a', 20_001)))));
        await (await UpdateAsync(t, product.Id.ToString(), UpdateBody(Guid.NewGuid(), product.Name, product.Slug, "1"))).ExpectErrorAsync(404, "NOT_FOUND");
        await (await UpdateAsync(t, Guid.NewGuid().ToString(), UpdateBody(category.Id, "x", "x", "1"))).ExpectErrorAsync(404, "NOT_FOUND");
        Assert.Single(await Outbox.AuditAsync(t)); // only the one successful create
    }

    [Fact]
    public async Task Duplicate_product_slug_is_SLUG_TAKEN()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (taken, _) = await Seed.ProductAsync(t, category.Id);
        var (other, _) = await Seed.ProductAsync(t, category.Id);

        await (await CreateAsync(t, Body(category.Id, slug: taken.Slug))).ExpectErrorAsync(409, "SLUG_TAKEN");
        await (await UpdateAsync(t, other.Id.ToString(), UpdateBody(category.Id, other.Name, taken.Slug, "1"))).ExpectErrorAsync(409, "SLUG_TAKEN");
    }

    [Fact]
    public async Task Stale_version_is_CONCURRENCY_CONFLICT()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id);
        var id = product.Id.ToString();

        await (await UpdateAsync(t, id, UpdateBody(category.Id, "First", product.Slug, "1"))).OkJsonAsync();

        await (await UpdateAsync(t, id, UpdateBody(category.Id, "Second", product.Slug, "1"))).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Concurrent_updates_with_the_same_version_leave_one_winner()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id);
        var id = product.Id.ToString();

        var responses = await Task.WhenAll(new[] { "First", "Second" }.Select(name => UpdateAsync(t, id, UpdateBody(category.Id, name, product.Slug, "1"))));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        await using var db = t.NewDb();
        var row = await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, row.Version);
        Assert.Single(await Outbox.AuditAsync(t)); // the loser wrote no audit
    }

    [Fact]
    public async Task Serialized_change_racing_the_first_receipt_has_one_winner()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var ct = TestContext.Current.CancellationToken;
        var category = await Seed.CategoryAsync(t);

        // 08-catalog §7: whichever runs first wins; the lock's answer and the final flag must always agree
        for (var i = 0; i < 6; i++)
        {
            var (product, skus) = await Seed.ProductAsync(t, category.Id);
            var update = UpdateAsync(t, product.Id.ToString(), UpdateBody(category.Id, product.Name, product.Slug, "1", serialized: true));
            var locked = t.Internal.LockSkuForStockAsync(new LockSkuForStockRequest { SkuId = skus[0].Id.ToString() }, cancellationToken: ct).ResponseAsync;

            var response = await update;
            var lockedAnswer = (await locked).Serialized;

            await using var db = t.NewDb();
            var row = await db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id, ct);
            Assert.True(row.SerializedLocked);
            Assert.Equal(row.Serialized, lockedAnswer);
            if (row.Serialized) await response.OkJsonAsync();
            else await response.ExpectErrorAsync(400, "SERIALIZED_LOCKED");
        }
    }

    [Fact]
    public async Task Search_vector_follows_name_category_and_description()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t, name: "Phones");
        var other = await Seed.CategoryAsync(t, name: "Tablets");

        var created = await (await CreateAsync(t, Body(category.Id, "Bloom Phone", "bloom-phone", md: "Waterproof design"))).OkJsonAsync();
        var id = created.GetProperty("id").GetString()!;
        Assert.True(await VectorMatchesAsync(t, id, "bloom"));
        Assert.True(await VectorMatchesAsync(t, id, "phone"));
        Assert.True(await VectorMatchesAsync(t, id, "waterproof"));
        Assert.False(await VectorMatchesAsync(t, id, "tablet"));

        // renaming the category refreshes the vector of its products
        var renamed = await t.SendAsync(HttpMethod.Put, $"/api/admin/catalog/categories/{category.Id}",
            new { name = "Handsets", slug = category.Slug, sortOrder = 0, version = "1" }, Manager);
        await renamed.OkJsonAsync();
        Assert.True(await VectorMatchesAsync(t, id, "handset"));

        // so does a name, description or category change on the product
        await (await UpdateAsync(t, id, UpdateBody(other.Id, "Bloom Slate", "bloom-phone", "1", md: "Lightweight"))).OkJsonAsync();
        Assert.True(await VectorMatchesAsync(t, id, "slate"));
        Assert.True(await VectorMatchesAsync(t, id, "tablet"));
        Assert.True(await VectorMatchesAsync(t, id, "lightweight"));
        Assert.False(await VectorMatchesAsync(t, id, "phone"));
        Assert.False(await VectorMatchesAsync(t, id, "handset"));
        Assert.False(await VectorMatchesAsync(t, id, "waterproof"));
    }

    [Fact]
    public async Task AdminGetProduct_shows_everything()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var created = await (await CreateAsync(t, Body(category.Id, md: "*hi*"))).OkJsonAsync();
        var id = created.GetProperty("id").GetString()!;
        await using (var db = t.NewDb())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE products SET serialized_locked = true WHERE id = {Guid.Parse(id)}", TestContext.Current.CancellationToken);
        }

        var product = await (await t.SendAsync(HttpMethod.Get, $"{Admin}/{id}", caller: Manager)).OkJsonAsync();

        Assert.Equal("draft", product.GetProperty("status").GetString());
        Assert.Equal("*hi*", product.GetProperty("descriptionMd").GetString());
        Assert.Equal("<p><em>hi</em></p>\n", product.GetProperty("descriptionHtml").GetString());
        Assert.True(product.GetProperty("serializedLocked").GetBoolean());
        Assert.Equal("1", product.GetProperty("version").GetString());

        await (await t.SendAsync(HttpMethod.Get, $"{Admin}/{Guid.NewGuid()}", caller: Manager)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await t.SendAsync(HttpMethod.Get, $"{Admin}/nope", caller: Manager)).ExpectErrorAsync(404, "NOT_FOUND");
    }

    [Fact]
    public async Task AdminGetProduct_includes_skus_inactive_included_and_both_image_statuses()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, skus) = await Seed.ProductAsync(t, category.Id, skuPrices: [500, 700]);
        await using (var db = t.NewDb())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE skus SET active = false WHERE id = {skus[1].Id}", TestContext.Current.CancellationToken);
            db.ProductImages.AddRange(
                new Data.ProductImage { Id = Guid.CreateVersion7(), ProductId = product.Id, StorageKey = "k/ready.png", Position = 1, Status = "ready", CreatedAt = t.Clock.GetUtcNow() },
                new Data.ProductImage { Id = Guid.CreateVersion7(), ProductId = product.Id, StorageKey = "k/pending.png", Position = 2, Status = "pending", CreatedAt = t.Clock.GetUtcNow() });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var shown = await (await t.SendAsync(HttpMethod.Get, $"{Admin}/{product.Id}", caller: Manager)).OkJsonAsync();

        Assert.Equal(2, shown.GetProperty("skus").GetArrayLength());
        Assert.Equal([true, false], shown.GetProperty("skus").EnumerateArray().Select(s => s.GetProperty("active").GetBoolean()).OrderByDescending(a => a));
        Assert.Equal(["ready", "pending"], shown.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("status").GetString()));
        Assert.EndsWith("/k/ready.png", shown.GetProperty("images")[0].GetProperty("url").GetString());
        Assert.Equal("500", shown.GetProperty("minPriceCents").GetString()); // active SKUs only
    }

    [Fact]
    public async Task AdminListProducts_filters_and_pages()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phones = await Seed.CategoryAsync(t);
        var tablets = await Seed.CategoryAsync(t);
        var (oldest, oldestSkus) = await Seed.ProductAsync(t, phones.Id, status: "draft");
        t.Clock.Advance(TimeSpan.FromMinutes(1));
        var (middle, _) = await Seed.ProductAsync(t, tablets.Id);
        t.Clock.Advance(TimeSpan.FromMinutes(1));
        var (newest, _) = await Seed.ProductAsync(t, phones.Id);

        async Task<JsonElement> ListAsync(string query) => await (await t.SendAsync(HttpMethod.Get, $"{Admin}{query}", caller: Manager)).OkJsonAsync();
        static string[] Ids(JsonElement list) => list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();

        var all = await ListAsync("");
        Assert.Equal([newest.Id.ToString(), middle.Id.ToString(), oldest.Id.ToString()], Ids(all));
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal([oldest.Id.ToString()], Ids(await ListAsync("?status=draft")));
        Assert.Equal([newest.Id.ToString(), oldest.Id.ToString()], Ids(await ListAsync($"?categoryId={phones.Id}")));
        Assert.Equal([oldest.Id.ToString()], Ids(await ListAsync($"?q={oldestSkus[0].Code.ToLowerInvariant()}")));
        Assert.Equal([middle.Id.ToString()], Ids(await ListAsync($"?q={middle.Slug.ToUpperInvariant()}")));
        Assert.Empty(Ids(await ListAsync("?q=%25"))); // LIKE wildcards are literal

        var second = await ListAsync("?pageSize=2&page=2");
        Assert.Equal([oldest.Id.ToString()], Ids(second));
        Assert.Equal(3, second.GetProperty("total").GetInt32());
        Assert.Equal("physical", all.GetProperty("items")[0].GetProperty("kind").GetString());

        Assert.Equal("page_size", await FieldAsync(await t.SendAsync(HttpMethod.Get, $"{Admin}?pageSize=101", caller: Manager)));
        Assert.Equal("status", await FieldAsync(await t.SendAsync(HttpMethod.Get, $"{Admin}?status=gone", caller: Manager)));
    }

    [Fact]
    public async Task Admin_reads_need_catalog_roles()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (product, _) = await Seed.ProductAsync(t, category.Id);

        foreach (var caller in new[] { TestCaller.Staff("support"), TestCaller.Customer(Guid.NewGuid()) })
        {
            await (await t.SendAsync(HttpMethod.Get, Admin, caller: caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await t.SendAsync(HttpMethod.Get, $"{Admin}/{product.Id}", caller: caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await CreateAsync(t, Body(category.Id), caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await UpdateAsync(t, product.Id.ToString(), UpdateBody(category.Id, "x", "x", "1"), caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        }
        await (await t.SendAsync(HttpMethod.Get, Admin, caller: TestCaller.Staff("admin"))).OkJsonAsync();
    }
}
