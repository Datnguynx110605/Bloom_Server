using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Tests;

public class SkuTests(Containers containers)
{
    private const string Admin = "/api/admin/catalog";
    private static readonly TestCaller Manager = TestCaller.Staff("catalog_manager");

    private static Task<HttpResponseMessage> PutAsync(TestCatalog t, string path, object body) => t.SendAsync(HttpMethod.Put, $"{Admin}{path}", body, Manager);

    private static Task<HttpResponseMessage> SetOptionsAsync(TestCatalog t, object productId, object optionTypes, object version) =>
        PutAsync(t, $"/products/{productId}/options", new { optionTypes, version });

    private static Task<HttpResponseMessage> CreateSkuAsync(TestCatalog t, object productId, string code, IEnumerable<Guid> values, long price = 99_900) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/products/{productId}/skus", new { code, optionValueIds = values, priceCents = price }, Manager);

    private static Task<HttpResponseMessage> PriceAsync(TestCatalog t, Guid sku, long price, object version) =>
        PutAsync(t, $"/skus/{sku}/price", new { priceCents = price, version });

    private static Task<HttpResponseMessage> ActiveAsync(TestCatalog t, Guid sku, bool active, object version) =>
        PutAsync(t, $"/skus/{sku}/active", new { active, version });

    private static Task<HttpResponseMessage> EligibilityAsync(TestCatalog t, Guid plan, IEnumerable<Guid> devices, object version) =>
        PutAsync(t, $"/products/{plan}/eligibility", new { eligibleProductIds = devices, version });

    private static async Task<JsonElement> GetAsync(TestCatalog t, Guid productId) =>
        await (await t.SendAsync(HttpMethod.Get, $"{Admin}/products/{productId}", caller: Manager)).OkJsonAsync();

    private static async Task<string> FieldAsync(HttpResponseMessage response, int status = 400) =>
        (await response.ExpectErrorAsync(status, "VALIDATION_FAILED")).GetProperty("metadata").GetProperty("field").GetString()!;

    /// <summary>The product's options as a request body that keeps every id, for tests that change one thing.</summary>
    private static JsonArray Types(JsonElement product) =>
        JsonNode.Parse(product.GetProperty("options").GetRawText())!.AsArray();

    private static object[] NewTypes() =>
    [
        new { name = "Storage", values = new[] { new { value = "256 GB" }, new { value = "512 GB" } } },
        new { name = "Color", values = new[] { new { value = "Black" }, new { value = "White" } } },
    ];

    [Fact]
    public async Task Options_then_skus()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (draft, _) = await Seed.ProductAsync(t, category.Id, "draft", skuPrices: []);

        var set = await (await SetOptionsAsync(t, draft.Id, NewTypes(), "1")).OkJsonAsync();

        Assert.Equal("2", set.GetProperty("version").GetString()); // product-level: bumps the product
        var storage = set.GetProperty("options")[0];
        var color = set.GetProperty("options")[1];
        Assert.Equal(["Storage", "Color"], set.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("name").GetString()));
        Assert.Equal(1, color.GetProperty("position").GetInt32());
        var s = storage.GetProperty("values").EnumerateArray().Select(v => Guid.Parse(v.GetProperty("id").GetString()!)).ToArray();
        var c = color.GetProperty("values").EnumerateArray().Select(v => Guid.Parse(v.GetProperty("id").GetString()!)).ToArray();

        long[] prices = [119_900, 99_900, 109_900, 129_900];
        (Guid S, Guid C)[] combos = [(s[1], c[0]), (s[0], c[0]), (s[0], c[1]), (s[1], c[1])];
        for (var i = 0; i < 4; i++)
        {
            var sku = await (await CreateSkuAsync(t, draft.Id, $"PH-{i}", [combos[i].C, combos[i].S], prices[i])).OkJsonAsync();
            Assert.Equal("1", sku.GetProperty("version").GetString());
            Assert.True(sku.GetProperty("active").GetBoolean());
            Assert.Equal(prices[i].ToString(), sku.GetProperty("priceCents").GetString());
            // option_value_ids come back in option-type order whatever order the request used
            Assert.Equal([combos[i].S.ToString(), combos[i].C.ToString()], sku.GetProperty("optionValueIds").EnumerateArray().Select(v => v.GetString()));
        }

        await using var db = t.NewDb();
        var row = await db.Skus.AsNoTracking().SingleAsync(x => x.Code == "PH-0", TestContext.Current.CancellationToken);
        Assert.Equal(string.Join(',', new[] { s[1].ToString(), c[0].ToString() }.Order(StringComparer.Ordinal)), row.OptionKey);
        Assert.Equal(2, await db.SkuOptionValues.CountAsync(l => l.SkuId == row.Id, TestContext.Current.CancellationToken));
        var product = await GetAsync(t, draft.Id);
        Assert.Equal("99900", product.GetProperty("minPriceCents").GetString());
        Assert.Equal("2", product.GetProperty("version").GetString()); // SKU writes leave the product version alone
        Assert.Equal(["catalog.options-set", .. Enumerable.Repeat("catalog.sku-created", 4)], (await Outbox.AuditAsync(t)).Select(e => e.Action));
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT search_vector @@ to_tsquery('english', 'black') AS \"Value\" FROM products WHERE id = {draft.Id}")
            .SingleAsync(TestContext.Current.CancellationToken)); // option values are in the vector (R12)
    }

    [Fact]
    public async Task C03_duplicate_option_combination_is_DUPLICATE_COMBINATION()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);

        await (await CreateSkuAsync(t, phone.Product.Id, "OTHER-CODE", [phone.Black, phone.Storage256])).ExpectErrorAsync(409, "DUPLICATE_COMBINATION");

        Assert.Empty(await Outbox.AuditAsync(t));
        await using var db = t.NewDb();
        Assert.Equal(4, await db.Skus.CountAsync(s => s.ProductId == phone.Product.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sku_needs_one_value_per_option_type()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var other = await Seed.PhoneAsync(t, category.Id, "Other Phone");

        Assert.Equal("option_value_ids", await FieldAsync(await CreateSkuAsync(t, phone.Product.Id, "NEW-1", [phone.Storage256])));
        Assert.Equal("option_value_ids", await FieldAsync(await CreateSkuAsync(t, phone.Product.Id, "NEW-2", [phone.Storage256, phone.Storage512])));
        Assert.Equal("option_value_ids", await FieldAsync(await CreateSkuAsync(t, phone.Product.Id, "NEW-3", [])));
        Assert.Equal("option_value_ids", await FieldAsync(await CreateSkuAsync(t, phone.Product.Id, "NEW-4", [phone.Storage256, phone.Black, phone.Black])));
        await (await CreateSkuAsync(t, phone.Product.Id, "NEW-5", [phone.Storage256, other.Black])).ExpectErrorAsync(404, "NOT_FOUND");
        await (await CreateSkuAsync(t, phone.Product.Id, "NEW-6", [phone.Storage256, Guid.NewGuid()])).ExpectErrorAsync(404, "NOT_FOUND");
        await (await CreateSkuAsync(t, Guid.NewGuid(), "NEW-7", [])).ExpectErrorAsync(404, "NOT_FOUND");
        Assert.Equal("code", await FieldAsync(await CreateSkuAsync(t, phone.Product.Id, "lower", [phone.Storage256, phone.Black])));
        Assert.Equal("code", await FieldAsync(await CreateSkuAsync(t, phone.Product.Id, "-X", [phone.Storage256, phone.Black])));
        Assert.Empty(await Outbox.AuditAsync(t));
    }

    [Fact]
    public async Task Duplicate_sku_code_is_SKU_CODE_TAKEN()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var (bare, _) = await Seed.ProductAsync(t, category.Id, "draft", skuPrices: []);

        await (await CreateSkuAsync(t, bare.Id, phone.Skus[0].Code, [])).ExpectErrorAsync(409, "SKU_CODE_TAKEN");
        await (await PutAsync(t, $"/skus/{phone.Skus[1].Id}", new { code = phone.Skus[0].Code, version = "1" })).ExpectErrorAsync(409, "SKU_CODE_TAKEN");

        Assert.Empty(await Outbox.AuditAsync(t));
    }

    [Fact]
    public async Task Update_sku_changes_code_and_audits_before_after()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var sku = phone.Skus[0];

        var updated = await (await PutAsync(t, $"/skus/{sku.Id}", new { code = "NEW-CODE", version = "1" })).OkJsonAsync();

        Assert.Equal("NEW-CODE", updated.GetProperty("code").GetString());
        Assert.Equal("2", updated.GetProperty("version").GetString());
        Assert.Equal(2, updated.GetProperty("optionValueIds").GetArrayLength());
        var entry = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal("catalog.sku-updated", entry.Action);
        Assert.Equal("sku", entry.TargetType);
        Assert.Equal(sku.Id.ToString(), entry.TargetId);
        Assert.Equal(sku.Code, entry.Details.GetProperty("before").GetProperty("code").GetString());
        Assert.Equal("NEW-CODE", entry.Details.GetProperty("after").GetProperty("code").GetString());

        await (await PutAsync(t, $"/skus/{sku.Id}", new { code = "NEW-CODE", version = "2" })).OkJsonAsync(); // unchanged: nothing written
        Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal("code", await FieldAsync(await PutAsync(t, $"/skus/{sku.Id}", new { code = "", version = "2" })));
        await (await PutAsync(t, $"/skus/{Guid.NewGuid()}", new { code = "X", version = "1" })).ExpectErrorAsync(404, "NOT_FOUND");
    }

    [Fact]
    public async Task Option_changes_that_break_skus_are_refused()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var product = await GetAsync(t, phone.Product.Id);

        var added = Types(product);
        added.Add(new JsonObject { ["name"] = "Size", ["values"] = new JsonArray(new JsonObject { ["value"] = "Big" }) });
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, phone.Product.Id, added, "1")));

        var removedType = Types(product);
        removedType.RemoveAt(1);
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, phone.Product.Id, removedType, "1")));

        var removedValue = Types(product);
        removedValue[0]!["values"]!.AsArray().RemoveAt(1);
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, phone.Product.Id, removedValue, "1")));

        var withoutIds = Types(product); // the same names without their ids are new types
        foreach (var type in withoutIds)
        {
            type!.AsObject().Remove("id");
            foreach (var value in type["values"]!.AsArray()) value!.AsObject().Remove("id");
        }
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, phone.Product.Id, withoutIds, "1")));
        Assert.Empty(await Outbox.AuditAsync(t));

        var renamed = Types(product); // a rename, a new value on a type, a swapped order: all fine
        renamed[0]!["values"]![0]!["value"] = "256GB";
        renamed[0]!["values"]!.AsArray().Add(new JsonObject { ["value"] = "1 TB" });
        var swapped = new JsonArray(renamed[1]!.DeepClone(), renamed[0]!.DeepClone());
        var after = await (await SetOptionsAsync(t, phone.Product.Id, swapped, "1")).OkJsonAsync();

        Assert.Equal("2", after.GetProperty("version").GetString());
        Assert.Equal(["Color", "Storage"], after.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("name").GetString()));
        var storage = after.GetProperty("options")[1];
        Assert.Equal(["256GB", "512 GB", "1 TB"], storage.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("value").GetString()));
        Assert.Equal(phone.Storage256.ToString(), storage.GetProperty("values")[0].GetProperty("id").GetString());
        Assert.Equal("catalog.options-set", Assert.Single(await Outbox.AuditAsync(t)).Action);

        await using var db = t.NewDb();
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT search_vector @@ to_tsquery('english', '256gb') AS \"Value\" FROM products WHERE id = {phone.Product.Id}")
            .SingleAsync(TestContext.Current.CancellationToken));

        // the same list again changes nothing
        await (await SetOptionsAsync(t, phone.Product.Id, JsonNode.Parse(after.GetProperty("options").GetRawText())!, "2")).OkJsonAsync();
        Assert.Single(await Outbox.AuditAsync(t));
    }

    [Fact]
    public async Task Options_bad_lists_are_VALIDATION_FAILED()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (draft, _) = await Seed.ProductAsync(t, category.Id, "draft", skuPrices: []);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var storage = new { name = "Storage", values = new[] { new { value = "256 GB" } } };

        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, draft.Id, new object[] { new { name = " ", values = new[] { new { value = "x" } } } }, "1")));
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, draft.Id, new object[] { new { name = "Color", values = Array.Empty<object>() } }, "1")));
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, draft.Id, new object[] { new { name = "Color", values = new[] { new { value = new string('v', 51) } } } }, "1")));
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, draft.Id, new object[] { storage, new { name = "STORAGE", values = new[] { new { value = "x" } } } }, "1")));
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, draft.Id, new object[] { new { name = "Color", values = new[] { new { value = "Red" }, new { value = " red " } } } }, "1")));
        await (await SetOptionsAsync(t, draft.Id, new object[] { new { id = phone.Storage256, name = "Storage", values = new[] { new { value = "x" } } } }, "1"))
            .ExpectErrorAsync(404, "NOT_FOUND"); // an id of another product's option type
        await (await SetOptionsAsync(t, Guid.NewGuid(), NewTypes(), "1")).ExpectErrorAsync(404, "NOT_FOUND");
        await (await SetOptionsAsync(t, draft.Id, NewTypes(), "9")).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        Assert.Empty(await Outbox.AuditAsync(t));

        await (await SetOptionsAsync(t, draft.Id, Array.Empty<object>(), "1")).OkJsonAsync(); // clearing an option-less product is a no-op
        Assert.Empty(await Outbox.AuditAsync(t));
    }

    [Fact]
    public async Task Price_change_updates_min_price_and_audits()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var sku = phone.Sku(phone.Storage256, phone.Black);
        await t.Redis.StringSetAsync($"product:{phone.Product.Id}", "x");
        await t.Redis.StringSetAsync("catalog:version", "7");

        var changed = await (await PriceAsync(t, sku.Id, 89_900, "1")).OkJsonAsync();

        Assert.Equal("89900", changed.GetProperty("priceCents").GetString());
        Assert.Equal("2", changed.GetProperty("version").GetString());
        Assert.Equal("89900", (await GetAsync(t, phone.Product.Id)).GetProperty("minPriceCents").GetString());
        var entry = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal("catalog.price-changed", entry.Action);
        Assert.Equal(sku.Id.ToString(), entry.TargetId);
        Assert.Equal(99_900, entry.Details.GetProperty("before").GetProperty("priceCents").GetInt64());
        Assert.Equal(89_900, entry.Details.GetProperty("after").GetProperty("priceCents").GetInt64());
        Assert.False(await t.Redis.KeyExistsAsync($"product:{phone.Product.Id}"));
        Assert.Equal(8, (long)await t.Redis.StringGetAsync("catalog:version"));

        await (await PriceAsync(t, sku.Id, 89_900, "2")).OkJsonAsync(); // same price: nothing written
        Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal("2", (await (await PriceAsync(t, sku.Id, 89_900, "2")).OkJsonAsync()).GetProperty("version").GetString());
    }

    [Fact]
    public async Task Deactivating_cheapest_sku_moves_min_price()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var cheap = new[] { phone.Sku(phone.Storage256, phone.Black), phone.Sku(phone.Storage256, phone.White) };
        var dear = new[] { phone.Sku(phone.Storage512, phone.Black), phone.Sku(phone.Storage512, phone.White) };

        foreach (var sku in cheap)
        {
            var off = await (await ActiveAsync(t, sku.Id, false, "1")).OkJsonAsync();
            Assert.False(off.GetProperty("active").GetBoolean());
            Assert.Equal("2", off.GetProperty("version").GetString());
        }
        Assert.Equal("119900", (await GetAsync(t, phone.Product.Id)).GetProperty("minPriceCents").GetString());

        await (await ActiveAsync(t, cheap[0].Id, false, "2")).OkJsonAsync(); // already off: nothing written
        Assert.Equal(2, (await Outbox.AuditAsync(t)).Count);

        foreach (var sku in dear) await (await ActiveAsync(t, sku.Id, false, "1")).OkJsonAsync();
        Assert.False((await GetAsync(t, phone.Product.Id)).TryGetProperty("minPriceCents", out _)); // none active

        var on = await (await ActiveAsync(t, cheap[0].Id, true, "2")).OkJsonAsync();
        Assert.True(on.GetProperty("active").GetBoolean());
        Assert.Equal("99900", (await GetAsync(t, phone.Product.Id)).GetProperty("minPriceCents").GetString());
        Assert.Equal(["catalog.sku-deactivated", "catalog.sku-deactivated", "catalog.sku-deactivated", "catalog.sku-deactivated", "catalog.sku-activated"],
            (await Outbox.AuditAsync(t)).Select(e => e.Action));
    }

    [Fact]
    public async Task Price_must_be_positive()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);

        Assert.Equal("price_cents", await FieldAsync(await PriceAsync(t, phone.Skus[0].Id, 0, "1")));
        Assert.Equal("price_cents", await FieldAsync(await PriceAsync(t, phone.Skus[0].Id, -5, "1")));
        Assert.Equal("price_cents", await FieldAsync(await CreateSkuAsync(t, phone.Product.Id, "NEW-1", [phone.Storage256, phone.Black], 0)));
        Assert.Empty(await Outbox.AuditAsync(t));
    }

    [Fact]
    public async Task Stale_sku_version_is_CONCURRENCY_CONFLICT()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var sku = phone.Skus[0];
        await (await PriceAsync(t, sku.Id, 89_900, "1")).OkJsonAsync();

        await (await PriceAsync(t, sku.Id, 79_900, "1")).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        await (await ActiveAsync(t, sku.Id, false, "1")).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        await (await PutAsync(t, $"/skus/{sku.Id}", new { code = "STALE", version = "1" })).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");

        await using var db = t.NewDb();
        var row = await db.Skus.AsNoTracking().SingleAsync(s => s.Id == sku.Id, TestContext.Current.CancellationToken);
        Assert.Equal(89_900, row.PriceCents);
        Assert.True(row.Active);
        Assert.Single(await Outbox.AuditAsync(t));
    }

    [Fact]
    public async Task Plan_has_one_sku_and_no_options()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var (plan, _) = await Seed.ProductAsync(t, category.Id, "draft", "warranty_plan", skuPrices: []);

        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, plan.Id, NewTypes(), "1")));
        Assert.Equal("option_types", await FieldAsync(await SetOptionsAsync(t, plan.Id, Array.Empty<object>(), "1")));

        var first = await (await CreateSkuAsync(t, plan.Id, "CARE-24", [], 9_900)).OkJsonAsync();
        Assert.Equal(0, first.GetProperty("optionValueIds").GetArrayLength());
        await (await CreateSkuAsync(t, plan.Id, "CARE-24-B", [], 9_900)).ExpectErrorAsync(409, "DUPLICATE_COMBINATION");
        Assert.Equal("9900", (await GetAsync(t, plan.Id)).GetProperty("minPriceCents").GetString());
    }

    [Fact]
    public async Task Plan_eligibility_rules()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var a = await Seed.PhoneAsync(t, category.Id, "Phone A");
        var b = await Seed.PhoneAsync(t, category.Id, "Phone B");
        var (plan, _) = await Seed.PlanAsync(t, category.Id, []);
        await t.Redis.StringSetAsync($"product:{a.Product.Id}", "x");
        await t.Redis.StringSetAsync($"product:{b.Product.Id}", "x");

        var set = await (await EligibilityAsync(t, plan.Id, [a.Product.Id, a.Product.Id], "1")).OkJsonAsync();

        Assert.Equal([a.Product.Id.ToString()], set.GetProperty("eligibleProductIds").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("2", set.GetProperty("version").GetString());
        var entry = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal("catalog.plan-eligibility-set", entry.Action);
        Assert.Equal(plan.Id.ToString(), entry.TargetId);
        Assert.Empty(entry.Details.GetProperty("before").GetProperty("eligibleProductIds").EnumerateArray());
        Assert.Equal([a.Product.Id.ToString()], entry.Details.GetProperty("after").GetProperty("eligibleProductIds").EnumerateArray().Select(x => x.GetString()));
        Assert.False(await t.Redis.KeyExistsAsync($"product:{a.Product.Id}"));
        Assert.True(await t.Redis.KeyExistsAsync($"product:{b.Product.Id}"));

        // replacing the set invalidates the old and the new device
        await t.Redis.StringSetAsync($"product:{a.Product.Id}", "x");
        var replaced = await (await EligibilityAsync(t, plan.Id, [b.Product.Id], "2")).OkJsonAsync();
        Assert.Equal([b.Product.Id.ToString()], replaced.GetProperty("eligibleProductIds").EnumerateArray().Select(x => x.GetString()));
        Assert.False(await t.Redis.KeyExistsAsync($"product:{a.Product.Id}"));
        Assert.False(await t.Redis.KeyExistsAsync($"product:{b.Product.Id}"));

        await (await EligibilityAsync(t, plan.Id, [b.Product.Id], "3")).OkJsonAsync(); // same set: nothing written
        Assert.Equal(2, (await Outbox.AuditAsync(t)).Count);

        Assert.Equal("plan_product_id", await FieldAsync(await EligibilityAsync(t, a.Product.Id, [b.Product.Id], "1")));
        Assert.Equal("eligible_product_ids", await FieldAsync(await EligibilityAsync(t, plan.Id, [plan.Id], "3")));
        await (await EligibilityAsync(t, plan.Id, [Guid.NewGuid()], "3")).ExpectErrorAsync(404, "NOT_FOUND");
        await (await EligibilityAsync(t, Guid.NewGuid(), [a.Product.Id], "1")).ExpectErrorAsync(404, "NOT_FOUND");
        await (await EligibilityAsync(t, plan.Id, [a.Product.Id], "1")).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        Assert.Equal(2, (await Outbox.AuditAsync(t)).Count);

        var cleared = await (await EligibilityAsync(t, plan.Id, [], "3")).OkJsonAsync();
        Assert.Equal(0, cleared.GetProperty("eligibleProductIds").GetArrayLength());
    }

    [Fact]
    public async Task Plan_price_change_deletes_eligible_device_pages()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var device = await Seed.PhoneAsync(t, category.Id);
        var unrelated = await Seed.PhoneAsync(t, category.Id, "Unrelated");
        var (_, sku) = await Seed.PlanAsync(t, category.Id, [device.Product.Id]);
        await t.Redis.StringSetAsync($"product:{device.Product.Id}", "x");
        await t.Redis.StringSetAsync($"product:{unrelated.Product.Id}", "x");

        await (await PriceAsync(t, sku.Id, 12_900, "1")).OkJsonAsync();

        Assert.False(await t.Redis.KeyExistsAsync($"product:{device.Product.Id}"));
        Assert.True(await t.Redis.KeyExistsAsync($"product:{unrelated.Product.Id}"));

        await t.Redis.StringSetAsync($"product:{device.Product.Id}", "x");
        await (await ActiveAsync(t, sku.Id, false, "2")).OkJsonAsync();
        Assert.False(await t.Redis.KeyExistsAsync($"product:{device.Product.Id}"));
    }

    [Fact]
    public async Task Sku_methods_need_a_catalog_role()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var (plan, _) = await Seed.PlanAsync(t, category.Id, []);

        foreach (var caller in new[] { TestCaller.Staff("support"), TestCaller.Customer(Guid.NewGuid()) })
        {
            var id = phone.Skus[0].Id;
            await (await t.SendAsync(HttpMethod.Put, $"{Admin}/products/{phone.Product.Id}/options", new { optionTypes = NewTypes(), version = "1" }, caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await t.SendAsync(HttpMethod.Post, $"{Admin}/products/{phone.Product.Id}/skus", new { code = "X", priceCents = 1 }, caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await t.SendAsync(HttpMethod.Put, $"{Admin}/skus/{id}", new { code = "X", version = "1" }, caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await t.SendAsync(HttpMethod.Put, $"{Admin}/skus/{id}/price", new { priceCents = 5, version = "1" }, caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await t.SendAsync(HttpMethod.Put, $"{Admin}/skus/{id}/active", new { active = false, version = "1" }, caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
            await (await t.SendAsync(HttpMethod.Put, $"{Admin}/products/{plan.Id}/eligibility", new { eligibleProductIds = Array.Empty<string>(), version = "1" }, caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        }
    }
}
