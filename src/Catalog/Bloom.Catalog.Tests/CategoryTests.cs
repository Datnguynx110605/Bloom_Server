using Bloom.Catalog.Validation;
using Microsoft.Extensions.Logging;

namespace Bloom.Catalog.Tests;

public class CategoryTests(Containers containers)
{
    private const string Admin = "/api/admin/catalog/categories";
    private const string Public = "/api/catalog/categories";
    private static readonly TestCaller Manager = TestCaller.Staff("catalog_manager");

    private static Task<HttpResponseMessage> CreateAsync(TestCatalog t, string name, string slug, string? parentId = null, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, Admin, new { parentId, name, slug, sortOrder = 0 }, caller ?? Manager);

    private static Task<HttpResponseMessage> UpdateAsync(
        TestCatalog t, string id, string name, string slug, string version, string? parentId = null, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Put, $"{Admin}/{id}", new { parentId, name, slug, sortOrder = 0, version }, caller ?? Manager);

    [Fact]
    public async Task Create_and_list_two_level_tree()
    {
        await using var t = await TestCatalog.StartAsync(containers);

        var top = await (await t.SendAsync(HttpMethod.Post, Admin, new { name = "Mac", slug = "mac", sortOrder = 2 }, Manager)).OkJsonAsync();
        var sub = await (await CreateAsync(t, "MacBook Pro", "macbook-pro", top.GetProperty("id").GetString())).OkJsonAsync();
        await (await t.SendAsync(HttpMethod.Post, Admin, new { name = "iPhone", slug = "iphone", sortOrder = 1 }, Manager)).OkJsonAsync();

        var tree = (await (await t.SendAsync(HttpMethod.Get, Public)).OkJsonAsync()).GetProperty("categories");

        Assert.Equal(["iPhone", "Mac"], tree.EnumerateArray().Select(c => c.GetProperty("name").GetString())); // by sort_order
        var mac = tree[1];
        Assert.Equal("1", mac.GetProperty("version").GetString());
        var child = Assert.Single(mac.GetProperty("children").EnumerateArray());
        Assert.Equal(sub.GetProperty("id").GetString(), child.GetProperty("id").GetString());
        Assert.Equal(top.GetProperty("id").GetString(), child.GetProperty("parentId").GetString());
        Assert.Equal("macbook-pro", child.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task C02_third_level_category_is_CATEGORY_DEPTH()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var a = await Seed.CategoryAsync(t);
        var b = await Seed.CategoryAsync(t);
        var sub = await Seed.CategoryAsync(t, a.Id);

        await (await CreateAsync(t, "Third", "third", sub.Id.ToString())).ExpectErrorAsync(400, "CATEGORY_DEPTH");
        await (await UpdateAsync(t, b.Id.ToString(), "B", "b", "1", sub.Id.ToString())).ExpectErrorAsync(400, "CATEGORY_DEPTH"); // under a sub
        await (await UpdateAsync(t, a.Id.ToString(), "A", "a", "1", b.Id.ToString())).ExpectErrorAsync(400, "CATEGORY_DEPTH"); // a has children
        await (await UpdateAsync(t, b.Id.ToString(), "B", "b", "1", b.Id.ToString())).ExpectErrorAsync(400, "CATEGORY_DEPTH"); // its own parent
    }

    [Fact]
    public async Task Duplicate_slug_is_SLUG_TAKEN()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var taken = await Seed.CategoryAsync(t);
        var other = await Seed.CategoryAsync(t);

        await (await CreateAsync(t, "Again", taken.Slug)).ExpectErrorAsync(409, "SLUG_TAKEN");
        await (await UpdateAsync(t, other.Id.ToString(), "Other", taken.Slug, "1")).ExpectErrorAsync(409, "SLUG_TAKEN");
    }

    [Fact]
    public async Task Bad_input_is_VALIDATION_FAILED()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var existing = await Seed.CategoryAsync(t);

        var slug = await (await CreateAsync(t, "Mac", "Not Valid")).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("slug", slug.GetProperty("metadata").GetProperty("field").GetString());
        var name = await (await CreateAsync(t, "", "mac")).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("name", name.GetProperty("metadata").GetProperty("field").GetString());
        var update = await (await UpdateAsync(t, existing.Id.ToString(), "  ", "mac", "1")).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("name", update.GetProperty("metadata").GetProperty("field").GetString());

        await (await CreateAsync(t, "Mac", "mac", Guid.NewGuid().ToString())).ExpectErrorAsync(404, "NOT_FOUND");
        await (await UpdateAsync(t, existing.Id.ToString(), "Mac", "mac", "1", Guid.NewGuid().ToString())).ExpectErrorAsync(404, "NOT_FOUND");
        await (await UpdateAsync(t, Guid.NewGuid().ToString(), "Mac", "mac", "1")).ExpectErrorAsync(404, "NOT_FOUND");
    }

    [Fact]
    public async Task Update_with_stale_version_is_CONCURRENCY_CONFLICT()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);

        var updated = await (await UpdateAsync(t, category.Id.ToString(), "Renamed", category.Slug, "1")).OkJsonAsync();
        Assert.Equal("2", updated.GetProperty("version").GetString());

        await (await UpdateAsync(t, category.Id.ToString(), "Again", category.Slug, "1")).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Writes_are_audited()
    {
        await using var t = await TestCatalog.StartAsync(containers);

        var created = await (await CreateAsync(t, "Mac", "mac")).OkJsonAsync();
        var id = created.GetProperty("id").GetString()!;
        await (await UpdateAsync(t, id, "Macs", "mac", "1")).OkJsonAsync();
        await (await UpdateAsync(t, id, "Macs", "mac", "2")).OkJsonAsync(); // no change: no write, no audit

        var entries = await Outbox.AuditAsync(t);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(Manager.Id, e.ActorId);
            Assert.Equal("catalog_manager", e.ActorRole);
            Assert.Equal("catalog", e.Service);
            Assert.Equal("category", e.TargetType);
            Assert.Equal(id, e.TargetId);
        });
        Assert.Equal("catalog.category-created", entries[0].Action);
        Assert.Empty(entries[0].Details.EnumerateObject());
        Assert.Equal("catalog.category-updated", entries[1].Action);
        var details = entries[1].Details;
        Assert.Equal(["after", "before"], details.EnumerateObject().Select(p => p.Name).Order()); // only the changed field
        Assert.Equal("Mac", details.GetProperty("before").GetProperty("name").GetString());
        Assert.Equal("Macs", details.GetProperty("after").GetProperty("name").GetString());
        Assert.Single(details.GetProperty("before").EnumerateObject());
        Assert.Single(details.GetProperty("after").EnumerateObject());
    }

    [Fact]
    public async Task Category_tree_is_cached_and_deleted_on_write()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var category = await Seed.CategoryAsync(t, name: "Before");

        await (await t.SendAsync(HttpMethod.Get, Public)).OkJsonAsync();
        Assert.True(await t.Redis.KeyExistsAsync("categories"));
        Assert.Equal(0, metrics.Count("catalog_read_seconds", ("endpoint", "list_categories"), ("cache", "hit")));
        await (await t.SendAsync(HttpMethod.Get, Public)).OkJsonAsync();
        Assert.Equal(1, metrics.Count("catalog_read_seconds", ("endpoint", "list_categories"), ("cache", "hit")));
        Assert.Equal(1, metrics.Count("catalog_read_seconds", ("endpoint", "list_categories"), ("cache", "miss")));

        await (await UpdateAsync(t, category.Id.ToString(), "After", category.Slug, "1")).OkJsonAsync();

        Assert.False(await t.Redis.KeyExistsAsync("categories"));
        var tree = (await (await t.SendAsync(HttpMethod.Get, Public)).OkJsonAsync()).GetProperty("categories");
        Assert.Equal("After", Assert.Single(tree.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Writes_need_catalog_roles()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);

        foreach (var caller in new[] { TestCaller.Staff("support"), TestCaller.Customer(Guid.NewGuid()) })
        {
            await (await CreateAsync(t, "Mac", "mac", caller: caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await UpdateAsync(t, category.Id.ToString(), "Mac", "mac", "1", caller: caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        }
        Assert.Empty(await Outbox.AuditAsync(t));
        await (await CreateAsync(t, "Admin's", "admins", caller: TestCaller.Staff("admin"))).OkJsonAsync();
    }

    [Fact]
    public async Task Redis_down_reads_and_writes_still_work()
    {
        await using var t = await TestCatalog.StartAsync(containers,
            o => o.Config["Catalog:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200");
        await Seed.CategoryAsync(t, name: "Seeded");

        var tree = (await (await t.SendAsync(HttpMethod.Get, Public)).OkJsonAsync()).GetProperty("categories");
        Assert.Equal("Seeded", Assert.Single(tree.EnumerateArray()).GetProperty("name").GetString());
        await (await CreateAsync(t, "Created", "created")).OkJsonAsync();

        Assert.Contains(t.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Cache invalidation failed"));
        await using var db = t.NewDb();
        Assert.Equal(2, db.Categories.Count());
    }

    [Fact]
    public async Task Failed_transaction_deletes_no_cache_key()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        await (await UpdateAsync(t, category.Id.ToString(), "Renamed", category.Slug, "1")).OkJsonAsync();
        await (await t.SendAsync(HttpMethod.Get, Public)).OkJsonAsync();
        Assert.True(await t.Redis.KeyExistsAsync("categories"));

        await (await UpdateAsync(t, category.Id.ToString(), "Stale", category.Slug, "1")).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");

        Assert.True(await t.Redis.KeyExistsAsync("categories"));
    }

    [Fact]
    public void Inputs_validate_text_slugs_and_paging()
    {
        Assert.Equal("Mac", Inputs.Name("  Mac ", "name", 100));
        Assert.Equal(3, Inputs.Name("😀😀😀", "name", 3).EnumerateRunes().Count()); // scalars, not UTF-16 units
        Assert.Equal("name", Field(() => Inputs.Name("  ", "name", 100)));
        Assert.Equal("name", Field(() => Inputs.Name("😀😀😀😀", "name", 3)));

        Assert.Equal("a-b-1", Inputs.Slug("a-b-1"));
        foreach (var bad in new[] { "", "A", "a--b", "-a", "a-", "a b", "a\n", new string('a', 101) })
            Assert.Equal("slug", Field(() => Inputs.Slug(bad)));

        Assert.Null(Inputs.OptionalText(null, "text", 5));
        Assert.Null(Inputs.OptionalText("  ", "text", 5));
        Assert.Equal("hi", Inputs.OptionalText(" hi ", "text", 5));
        Assert.Equal("text", Field(() => Inputs.OptionalText("abcdef", "text", 5)));

        Assert.Equal((1, 24), Inputs.Paging(0, 0, 24, 48));
        Assert.Equal((3, 48), Inputs.Paging(3, 48, 24, 48));
        Assert.Equal("page_size", Field(() => Inputs.Paging(1, 49, 24, 48)));
        Assert.Equal("page_size", Field(() => Inputs.Paging(1, -1, 24, 48)));
        Assert.Equal("page", Field(() => Inputs.Paging(-1, 0, 24, 48)));
    }

    private static string? Field(Action action)
    {
        var e = Assert.Throws<Grpc.Core.RpcException>(action);
        return Google.Rpc.Status.Parser.ParseFrom(e.Trailers.GetValueBytes("grpc-status-details-bin")).Details[0]
            .Unpack<Google.Rpc.ErrorInfo>().Metadata["field"];
    }
}
