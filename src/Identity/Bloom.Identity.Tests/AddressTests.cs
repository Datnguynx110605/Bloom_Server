using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Tests;

public class AddressTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, object?> Body(string recipient = "Ann Lee", bool isDefault = false) => new()
    {
        ["label"] = "Home", ["recipient"] = recipient, ["line1"] = "1 Main St", ["line2"] = "", ["city"] = "Austin",
        ["state"] = "TX", ["zip"] = "78701", ["phone"] = "+15125550100", ["isDefault"] = isDefault,
    };

    private static Task<HttpResponseMessage> UpsertAsync(TestIdentity t, TestCaller caller, Dictionary<string, object?> body) =>
        t.SendAsync(HttpMethod.Put, "/api/me/addresses", body, caller);

    private static async Task<JsonElement> ListAsync(TestIdentity t, TestCaller caller) =>
        (await (await t.SendAsync(HttpMethod.Get, "/api/me/addresses", caller: caller)).OkJsonAsync()).GetProperty("addresses");

    [Fact]
    public async Task Create_list_update_delete()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var caller = TestCaller.Customer(t, await Seed.CustomerAsync(t));

        var created = await (await UpsertAsync(t, caller, Body())).OkJsonAsync();
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(1, created.GetProperty("version").GetInt64());
        Assert.Equal("+15125550100", created.GetProperty("phone").GetString());
        Assert.Equal(1, (await ListAsync(t, caller)).GetArrayLength());

        var update = Body("  Bea Lee  ");
        update["id"] = id;
        update["version"] = 1;
        var updated = await (await UpsertAsync(t, caller, update)).OkJsonAsync();
        Assert.Equal(id, updated.GetProperty("id").GetString());
        Assert.Equal(2, updated.GetProperty("version").GetInt64());
        Assert.Equal("Bea Lee", updated.GetProperty("recipient").GetString());
        Assert.Equal("Bea Lee", (await ListAsync(t, caller))[0].GetProperty("recipient").GetString());

        Assert.Equal(HttpStatusCode.OK, (await t.SendAsync(HttpMethod.Delete, $"/api/me/addresses/{id}", caller: caller)).StatusCode);
        Assert.Equal(0, (await ListAsync(t, caller)).GetArrayLength());
    }

    [Fact]
    public async Task Stale_version_is_CONCURRENCY_CONFLICT()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var caller = TestCaller.Customer(t, await Seed.CustomerAsync(t));
        var id = (await (await UpsertAsync(t, caller, Body())).OkJsonAsync()).GetProperty("id").GetString();

        var update = Body("Bea Lee");
        update["id"] = id;
        update["version"] = 1;
        await (await UpsertAsync(t, caller, update)).OkJsonAsync();
        await (await UpsertAsync(t, caller, update)).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Other_accounts_address_is_NOT_FOUND()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var owner = TestCaller.Customer(t, await Seed.CustomerAsync(t));
        var other = TestCaller.Customer(t, await Seed.CustomerAsync(t));
        var id = (await (await UpsertAsync(t, owner, Body())).OkJsonAsync()).GetProperty("id").GetString();

        var update = Body();
        update["id"] = id;
        update["version"] = 1;
        await (await UpsertAsync(t, other, update)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await t.SendAsync(HttpMethod.Delete, $"/api/me/addresses/{id}", caller: other)).ExpectErrorAsync(404, "NOT_FOUND");

        update["id"] = Guid.NewGuid().ToString("D");
        await (await UpsertAsync(t, owner, update)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await t.SendAsync(HttpMethod.Delete, $"/api/me/addresses/{Guid.NewGuid():D}", caller: owner)).ExpectErrorAsync(404, "NOT_FOUND");
        Assert.Equal(1, (await ListAsync(t, owner)).GetArrayLength());
    }

    [Fact]
    public async Task Eleventh_address_is_refused()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var caller = TestCaller.Customer(t, await Seed.CustomerAsync(t));
        for (var i = 0; i < 10; i++) await (await UpsertAsync(t, caller, Body())).OkJsonAsync();

        await (await UpsertAsync(t, caller, Body())).ExpectErrorAsync(400, "ADDRESS_LIMIT_REACHED");
        Assert.Equal(10, (await ListAsync(t, caller)).GetArrayLength());
    }

    [Fact]
    public async Task Default_moves_to_the_new_default()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var caller = TestCaller.Customer(t, await Seed.CustomerAsync(t));
        var first = (await (await UpsertAsync(t, caller, Body(isDefault: true))).OkJsonAsync()).GetProperty("id").GetString();
        var second = (await (await UpsertAsync(t, caller, Body(isDefault: true))).OkJsonAsync()).GetProperty("id").GetString();

        async Task<List<string?>> DefaultsAsync() =>
            (await ListAsync(t, caller)).EnumerateArray().Where(a => a.TryGetProperty("isDefault", out var d) && d.GetBoolean())
            .Select(a => a.GetProperty("id").GetString()).ToList();

        Assert.Equal([second], await DefaultsAsync());

        var update = Body(isDefault: true);
        update["id"] = first;
        update["version"] = 2; // losing the default is a change to the row: version 1 -> 2
        var moved = await UpsertAsync(t, caller, update);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal([first], await DefaultsAsync());
    }

    [Theory]
    [InlineData("state", "XX")]
    [InlineData("zip", "1234")]
    [InlineData("phone", "555-1234")]
    [InlineData("recipient", "")]
    [InlineData("line1", "LONG")]
    public async Task Invalid_field_is_VALIDATION_FAILED(string field, string value)
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var caller = TestCaller.Customer(t, await Seed.CustomerAsync(t));
        var body = Body();
        body[field] = value == "LONG" ? new string('a', 101) : value;

        var info = await (await UpsertAsync(t, caller, body)).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal(field, info.GetProperty("metadata").GetProperty("field").GetString());
        Assert.Equal(0, (await ListAsync(t, caller)).GetArrayLength());
    }

    [Fact]
    public async Task Staff_cannot_use_addresses()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (staff, _) = await Seed.StaffAsync(t);
        var caller = TestCaller.Staff(staff.Id, staff.Role!, t.Clock.GetUtcNow());

        await (await t.SendAsync(HttpMethod.Get, "/api/me/addresses", caller: caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        await (await UpsertAsync(t, caller, Body())).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        await (await t.SendAsync(HttpMethod.Delete, $"/api/me/addresses/{Guid.NewGuid():D}", caller: caller)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }
}
