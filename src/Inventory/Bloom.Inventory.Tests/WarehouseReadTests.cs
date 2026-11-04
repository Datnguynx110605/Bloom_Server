using System.Text.Json;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class WarehouseReadTests(Containers containers)
{
    private const string Base = "/api/admin/inventory";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, bool> RoleMatrix()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var method in new[] { "ListStockLevels", "GetLedger", "GetSerial" })
            foreach (var role in new[] { "warehouse", "catalog_manager", "support", "admin", "fulfillment" })
                data.Add(method, role, (method, role) switch
                {
                    ("ListStockLevels", "warehouse" or "catalog_manager" or "support" or "admin") => true,
                    ("GetLedger", "warehouse" or "admin") => true,
                    ("GetSerial", "warehouse" or "support" or "admin") => true,
                    _ => false,
                });
        return data;
    }

    private static Task<HttpResponseMessage> GetAsync(TestInventory t, string path, TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Get, Base + path, caller: caller ?? TestCaller.Staff("warehouse"));

    private static Task ReserveAsync(TestInventory t, Guid sku, int quantity) =>
        t.Stock.ReserveStockAsync(new() { OrderId = Guid.CreateVersion7().ToString(), Lines = { new StockLine { SkuId = sku.ToString(), Quantity = quantity } } }, cancellationToken: Ct).ResponseAsync;

    private static string[] SkuIds(JsonElement body) => body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("skuId").GetString()!).ToArray();

    /// <summary>Postgres orders uuids bytewise, which is the order of their lower-case hex text.</summary>
    private static string[] Sorted(params Guid[] ids) => ids.Select(i => i.ToString()).Order(StringComparer.Ordinal).ToArray();

    private static async Task ExpectValidationAsync(HttpResponseMessage response, string field)
    {
        var info = await response.ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal(field, info.GetProperty("metadata").GetProperty("field").GetString());
    }

    [Fact]
    public async Task Stock_levels_filter_and_page()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var p1 = Guid.CreateVersion7();
        var p2 = Guid.CreateVersion7();
        Guid a = Guid.CreateVersion7(), b = Guid.CreateVersion7(), c = Guid.CreateVersion7();
        t.Catalog.AddSku(a, p1, false);
        t.Catalog.AddSku(b, p1, false);
        t.Catalog.AddSku(c, p2, false);
        await Seed.ReceiveAsync(t, a, 3);
        await Seed.ReceiveAsync(t, b, 5);
        await Seed.ReceiveAsync(t, c, 2);
        await ReserveAsync(t, a, 3); // a is sold out
        await Seed.BackorderAsync(t, a, 4, DateTimeOffset.UtcNow);
        await Seed.BackorderAsync(t, a, 2, DateTimeOffset.UtcNow);

        async Task<string[]> ListAsync(string query)
        {
            using var response = await GetAsync(t, "/stock" + query);
            return SkuIds(await response.OkJsonAsync());
        }

        Assert.Equal(Sorted(a, b, c), await ListAsync(""));
        Assert.Equal(Sorted(a, b), await ListAsync($"?productId={p1}"));
        Assert.Equal(Sorted(b, c), await ListAsync($"?skuIds={b}&skuIds={c}"));
        Assert.Equal([a.ToString()], await ListAsync("?availableAtMost=0"));
        Assert.Equal(Sorted(a, c), await ListAsync("?availableAtMost=2"));
        Assert.Equal([a.ToString()], await ListAsync("?hasBackorders=true"));
        Assert.Equal(Sorted(b, c), await ListAsync("?hasBackorders=false"));
        Assert.Equal([a.ToString()], await ListAsync($"?productId={p1}&availableAtMost=4&hasBackorders=true")); // AND
        Assert.Empty(await ListAsync($"?productId={p2}&hasBackorders=true"));

        var all = Sorted(a, b, c);
        using (var first = await GetAsync(t, "/stock?pageSize=2&page=1")) Assert.Equal(all[..2], SkuIds(await first.OkJsonAsync()));
        using (var second = await GetAsync(t, "/stock?pageSize=2&page=2")) Assert.Equal(all[2..], SkuIds(await second.OkJsonAsync()));
        Assert.Empty(await ListAsync("?pageSize=2&page=3"));

        using var one = await GetAsync(t, $"/stock?skuIds={a}");
        var item = (await one.OkJsonAsync()).GetProperty("items")[0];
        Assert.Equal(p1.ToString(), item.GetProperty("productId").GetString());
        Assert.False(item.GetProperty("serialized").GetBoolean());
        Assert.Equal(3, item.GetProperty("onHand").GetInt32());
        Assert.Equal(3, item.GetProperty("reserved").GetInt32());
        Assert.Equal(0, item.GetProperty("available").GetInt32());
        Assert.Equal(0, item.GetProperty("committed").GetInt32());
        Assert.Equal(0, item.GetProperty("returned").GetInt32());
        Assert.Equal(0, item.GetProperty("damaged").GetInt32());
        Assert.Equal(0, item.GetProperty("lost").GetInt32());
        Assert.Equal(6, item.GetProperty("backordered").GetInt32());
        Assert.Equal((await Seed.ItemAsync(t, a))!.UpdatedAt, item.GetProperty("updatedAt").GetDateTimeOffset());

        using var negative = await GetAsync(t, "/stock?availableAtMost=-1");
        await ExpectValidationAsync(negative, "available_at_most");
        using var badProduct = await GetAsync(t, "/stock?productId=nope");
        await ExpectValidationAsync(badProduct, "product_id");
        using var badSku = await GetAsync(t, $"/stock?skuIds={a}&skuIds=nope");
        await ExpectValidationAsync(badSku, "sku_ids");
    }

    [Fact]
    public async Task Never_received_skus_are_not_listed()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var received = Seed.Sku(t);
        var never = Seed.Sku(t);
        await Seed.BackorderAsync(t, never, 1, DateTimeOffset.UtcNow); // a pre-order exists, a stock_items row does not
        await Seed.ReceiveAsync(t, received, 1);

        using var response = await GetAsync(t, $"/stock?skuIds={received}&skuIds={never}");
        Assert.Equal([received.ToString()], SkuIds(await response.OkJsonAsync()));
    }

    [Fact]
    public async Task Ledger_is_newest_first_in_range()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var now = t.Clock.GetUtcNow();
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero).AddSeconds(1); // whole seconds, as the query string carries
        t.Clock.SetUtcNow(start);
        var sku = Seed.Sku(t);
        var staff = TestCaller.Staff("warehouse");
        var receipts = new List<Guid>();
        for (var i = 0; i < 4; i++) // one ledger row each, a minute apart
        {
            var body = new { lines = new[] { new { skuId = sku, quantity = i + 1, serials = Array.Empty<string>() } } };
            using var posted = await t.SendAsync(HttpMethod.Post, Base + "/receipts", body, staff, Guid.NewGuid());
            receipts.Add(Guid.Parse((await posted.OkJsonAsync()).GetProperty("receiptId").GetString()!));
            t.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        async Task<JsonElement[]> EntriesAsync(string query)
        {
            using var response = await GetAsync(t, $"/ledger/{sku}{query}", staff);
            return (await response.OkJsonAsync()).GetProperty("entries").EnumerateArray().Select(e => e.Clone()).ToArray();
        }
        static string At(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var all = await EntriesAsync("");
        Assert.Equal(receipts.AsEnumerable().Reverse().Select(r => r.ToString()), all.Select(e => e.GetProperty("refId").GetString()));
        var newest = all[0];
        Assert.Equal(sku.ToString(), newest.GetProperty("skuId").GetString());
        Assert.Equal("received", newest.GetProperty("entryType").GetString());
        Assert.Equal(4, newest.GetProperty("dOnHand").GetInt32());
        Assert.Equal(0, newest.GetProperty("dReserved").GetInt32());
        Assert.Equal(0, newest.GetProperty("dCommitted").GetInt32());
        Assert.Equal(0, newest.GetProperty("dReturned").GetInt32());
        Assert.Equal(0, newest.GetProperty("dDamaged").GetInt32());
        Assert.Equal(0, newest.GetProperty("dLost").GetInt32());
        Assert.Equal("receipt", newest.GetProperty("refType").GetString());
        Assert.Equal(staff.Id.ToString(), newest.GetProperty("actor").GetString());
        Assert.Equal(start.AddMinutes(3), newest.GetProperty("createdAt").GetDateTimeOffset());
        Assert.False(string.IsNullOrEmpty(newest.GetProperty("id").GetString()));

        var range = await EntriesAsync($"?from={At(start.AddMinutes(1))}&to={At(start.AddMinutes(3))}"); // from inclusive, to exclusive
        Assert.Equal([receipts[2].ToString(), receipts[1].ToString()], range.Select(e => e.GetProperty("refId").GetString()));
        Assert.Equal(3, (await EntriesAsync($"?to={At(start.AddMinutes(3))}")).Length);
        Assert.Single(await EntriesAsync($"?from={At(start.AddMinutes(3))}"));

        Assert.Equal(all[..3].Select(e => e.GetProperty("id").GetString()), (await EntriesAsync("?pageSize=3")).Select(e => e.GetProperty("id").GetString()));
        Assert.Equal(all[3..].Select(e => e.GetProperty("id").GetString()), (await EntriesAsync("?pageSize=3&page=2")).Select(e => e.GetProperty("id").GetString()));
        Assert.Empty(await EntriesAsync("?pageSize=3&page=3"));

        using var unknown = await GetAsync(t, $"/ledger/{Guid.CreateVersion7()}");
        Assert.Empty((await unknown.OkJsonAsync()).GetProperty("entries").EnumerateArray());
        using var malformed = await GetAsync(t, "/ledger/nope");
        await ExpectValidationAsync(malformed, "sku_id");
    }

    [Fact]
    public async Task Get_serial_shows_status_and_ref()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t, serialized: true);
        var receiptId = await Seed.ReceiveAsync(t, sku, 2, ["ABC123", "XYZ9"]);
        var refId = Guid.CreateVersion7();
        await using (var db = t.NewDb())
            await db.Database.ExecuteSqlAsync($"UPDATE serial_units SET status = {SerialStatuses.Assigned}, ref_id = {refId} WHERE serial = 'ABC123'", Ct);

        using var assigned = await GetAsync(t, "/serials/%20abc123%20", TestCaller.Staff("support"));
        var unit = await assigned.OkJsonAsync();
        Assert.Equal("ABC123", unit.GetProperty("serial").GetString());
        Assert.Equal(sku.ToString(), unit.GetProperty("skuId").GetString());
        Assert.Equal("assigned", unit.GetProperty("status").GetString());
        Assert.Equal(refId.ToString(), unit.GetProperty("refId").GetString());
        Assert.Equal(receiptId.ToString(), unit.GetProperty("receiptId").GetString());
        Assert.True(unit.TryGetProperty("updatedAt", out _));

        using var inStock = await GetAsync(t, "/serials/xyz9");
        var free = await inStock.OkJsonAsync();
        Assert.Equal("in_stock", free.GetProperty("status").GetString());
        Assert.True(!free.TryGetProperty("refId", out var none) || string.IsNullOrEmpty(none.GetString()));

        using var unknown = await GetAsync(t, "/serials/NOPE1");
        await unknown.ExpectErrorAsync(404, "NOT_FOUND");
        using var malformed = await GetAsync(t, "/serials/bad-serial!");
        await ExpectValidationAsync(malformed, "serial");
    }

    [Theory, MemberData(nameof(RoleMatrix))]
    public async Task Read_roles(string method, string role, bool allowed)
    {
        await using var t = await TestInventory.StartAsync(containers);
        var path = method switch
        {
            "ListStockLevels" => "/stock",
            "GetLedger" => $"/ledger/{Guid.CreateVersion7()}",
            _ => "/serials/NOPE1",
        };

        using var response = await GetAsync(t, path, TestCaller.Staff(role));

        if (!allowed) await response.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        else if (method == "GetSerial") await response.ExpectErrorAsync(404, "NOT_FOUND"); // past the role check, no such serial
        else await response.OkJsonAsync();
    }

    [Fact]
    public async Task Customers_and_the_role_check_runs_before_validation()
    {
        await using var t = await TestInventory.StartAsync(containers);

        using var customer = await GetAsync(t, "/stock", new TestCaller(Guid.NewGuid(), "customer", null));
        await customer.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        using var refused = await GetAsync(t, "/ledger/nope", TestCaller.Staff("support"));
        await refused.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }

    [Theory]
    [InlineData("/stock?pageSize=101")]
    [InlineData("/stock?pageSize=-1")]
    [InlineData("/stock?page=-1")]
    [InlineData("/ledger/00000000-0000-7000-8000-000000000001?pageSize=101")]
    public async Task Page_size_out_of_range_is_VALIDATION_FAILED(string path)
    {
        await using var t = await TestInventory.StartAsync(containers);

        using var response = await GetAsync(t, path);

        await ExpectValidationAsync(response, path.Contains("page=") ? "page" : "page_size");
    }
}
