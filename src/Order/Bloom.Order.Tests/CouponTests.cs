using Bloom.Order.Api;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using ApiCoupon = Bloom.Order.Api.Coupon;

namespace Bloom.Order.Tests;

public class CouponTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static string NewId() => Guid.NewGuid().ToString("D");

    private static CreateCouponRequest Valid(string code = "SAVE-10") => new() { Code = code, Type = "percent", Value = 10, Scope = "order" };

    private static ScopeItem Item(string type = "product", string? id = null) => new() { TargetType = type, TargetId = id ?? NewId() };

    private static Timestamp At(DateTimeOffset t) => Timestamp.FromDateTimeOffset(t);

    private static async Task<ApiCoupon> CreateAsync(TestOrder t, string code, TestCaller? caller = null, Action<CreateCouponRequest>? edit = null)
    {
        var request = Valid(code);
        edit?.Invoke(request);
        return await t.Rpc.Staff.CreateCouponAsync(request, (caller ?? TestCaller.Staff("catalog_manager")).Call());
    }

    private static UpdateCouponRequest UpdateOf(ApiCoupon c) => new()
    {
        CouponId = c.Id, Version = c.Version, Type = c.Type, Value = c.Value, Scope = c.Scope, StartsAt = c.StartsAt, ExpiresAt = c.ExpiresAt,
        ScopeItems = { c.ScopeItems },
    };

    private static async Task<int> AuditCountAsync(TestOrder t, string action) => (await Outbox.AuditAsync(t)).Count(a => a.Action == action);

    [Fact]
    public async Task Create_coupon_normalizes_code_and_audits()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var staff = TestCaller.Staff("catalog_manager");

        var coupon = await CreateAsync(t, " Save-10 ", staff, r => { r.Type = "fixed"; r.Value = 500; r.MinSpendCents = 2500; r.TotalLimit = 100; });

        Assert.Equal("Save-10", coupon.Code);
        Assert.Equal(("fixed", 500, 2500, 100), (coupon.Type, coupon.Value, coupon.MinSpendCents, coupon.TotalLimit));
        Assert.True(coupon.Active);
        Assert.Equal((0, 0L), (coupon.UsesCount, coupon.Version));
        Assert.Equal(t.Clock.GetUtcNow(), coupon.CreatedAt.ToDateTimeOffset());
        await using var db = t.NewDb();
        var row = await db.Coupons.SingleAsync(Ct);
        Assert.Equal(("SAVE-10", "Save-10"), (row.CodeNormalized, row.CodeDisplay));
        var audit = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal(("order", "order.create-coupon", "coupon", coupon.Id, staff.Id, "catalog_manager"), (audit.Service, audit.Action, audit.TargetType, audit.TargetId, audit.ActorId, audit.ActorRole));
    }

    [Fact]
    public async Task Code_taken_ignoring_case_is_COUPON_CODE_TAKEN()
    {
        await using var t = await TestOrder.StartAsync(containers);
        await CreateAsync(t, "Save-10");

        await Rpc.FailsAsync(t.Rpc.Staff.CreateCouponAsync(Valid(" SAVE-10"), TestCaller.Staff("admin").Call()), "COUPON_CODE_TAKEN");
        // Concurrent creates of one new code: the unique index decides, so exactly one wins and the rest are COUPON_CODE_TAKEN.
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            try { await CreateAsync(t, i % 2 == 0 ? "Race-1" : "RACE-1"); return null; }
            catch (RpcException e) { return e; }
        }));
        var failed = results.OfType<RpcException>().ToList();
        Assert.Equal(4, failed.Count);
        Assert.All(failed, e => Assert.Equal((StatusCode.AlreadyExists, "COUPON_CODE_TAKEN"), (e.StatusCode, Rpc.Info(e).Reason)));
        await using var db = t.NewDb();
        Assert.Equal(2, await db.Coupons.CountAsync(Ct));
        Assert.Equal(2, await AuditCountAsync(t, "order.create-coupon")); // a refused create leaves no audit
    }

    public static TheoryData<string, Action<CreateCouponRequest>> BadFields => new()
    {
        { "code", r => r.Code = "ab" },
        { "code", r => r.Code = "save 10" },
        { "type", r => r.Type = "bogus" },
        { "value", r => r.Value = 0 },
        { "value", r => r.Value = 101 },
        { "value", r => (r.Type, r.Value) = ("fixed", 0) },
        { "scope", r => r.Scope = "bogus" },
        { "scope_items", r => r.Scope = "selected" },
        { "scope_items", r => r.ScopeItems.Add(Item()) }, // scope = order takes no items
        { "target_type", r => { r.Scope = "selected"; r.ScopeItems.Add(Item("brand")); } },
        { "target_id", r => { r.Scope = "selected"; r.ScopeItems.Add(Item("product", "not-a-uuid")); } },
        { "min_spend_cents", r => r.MinSpendCents = 0 },
        { "total_limit", r => r.TotalLimit = 0 },
        { "per_customer_limit", r => r.PerCustomerLimit = 0 },
        { "expires_at", r => (r.StartsAt, r.ExpiresAt) = (At(DateTimeOffset.UnixEpoch.AddDays(2)), At(DateTimeOffset.UnixEpoch.AddDays(2))) },
    };

    [Theory, MemberData(nameof(BadFields))]
    public async Task Coupon_field_rules(string field, Action<CreateCouponRequest> mutate)
    {
        await using var t = await TestOrder.StartAsync(containers);
        var request = Valid();
        mutate(request);

        var info = await Rpc.FailsAsync(t.Rpc.Staff.CreateCouponAsync(request, TestCaller.Staff("admin").Call()), "VALIDATION_FAILED");

        Assert.Equal(field, info.Metadata["field"]);
        await using var db = t.NewDb();
        Assert.False(await db.Coupons.AnyAsync(Ct));
    }

    [Fact]
    public async Task Update_changes_every_field_but_the_code()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var staff = TestCaller.Staff("admin");
        var first = Item("product");
        var created = await CreateAsync(t, "Save-10", staff, r => { r.Scope = "selected"; r.ScopeItems.Add([first, Item("category")]); });
        var starts = t.Clock.GetUtcNow().AddDays(1);
        var next = Item("category");

        var updated = await t.Rpc.Staff.UpdateCouponAsync(new UpdateCouponRequest
        {
            CouponId = created.Id, Version = created.Version, Type = "fixed", Value = 750, Scope = "selected", ScopeItems = { next },
            MinSpendCents = 5000, StartsAt = At(starts), ExpiresAt = At(starts.AddDays(7)), TotalLimit = 50, PerCustomerLimit = 2,
        }, staff.Call());

        Assert.Equal((created.Id, "Save-10", 1L), (updated.Id, updated.Code, updated.Version));
        Assert.Equal(("fixed", 750, "selected", 5000, 50, 2, true), (updated.Type, updated.Value, updated.Scope, updated.MinSpendCents, updated.TotalLimit, updated.PerCustomerLimit, updated.Active));
        Assert.Equal((starts, starts.AddDays(7)), (updated.StartsAt.ToDateTimeOffset(), updated.ExpiresAt.ToDateTimeOffset()));
        Assert.Equal([(next.TargetType, next.TargetId)], updated.ScopeItems.Select(i => (i.TargetType, i.TargetId)));
        await using (var db = t.NewDb())
            Assert.Equal([Guid.Parse(next.TargetId)], await db.CouponScopeItems.Select(i => i.TargetId).ToListAsync(Ct)); // the old items are gone

        // Optional fields clear when absent, and the scope can go back to the whole order; an inactive coupon stays inactive.
        var off = await t.Rpc.Staff.DeactivateCouponAsync(new DeactivateCouponRequest { CouponId = created.Id, Version = updated.Version }, staff.Call());
        var cleared = await t.Rpc.Staff.UpdateCouponAsync(new UpdateCouponRequest { CouponId = created.Id, Version = off.Version, Type = "percent", Value = 5, Scope = "order" }, staff.Call());
        Assert.Equal((3L, false, 5, false, false, false), (cleared.Version, cleared.Active, cleared.Value, cleared.HasMinSpendCents, cleared.HasTotalLimit, cleared.StartsAt is not null));
        Assert.Empty(cleared.ScopeItems);
        Assert.Equal(["order.create-coupon", "order.update-coupon", "order.deactivate-coupon", "order.update-coupon"], (await Outbox.AuditAsync(t)).Select(a => a.Action));
        Assert.All(await Outbox.AuditAsync(t), a => Assert.Equal(created.Id, a.TargetId));

        await Rpc.FailsAsync(t.Rpc.Staff.UpdateCouponAsync(UpdateOf(updated), staff.Call()), "CONCURRENCY_CONFLICT");
        var unknown = UpdateOf(updated);
        unknown.CouponId = NewId();
        await Rpc.FailsAsync(t.Rpc.Staff.UpdateCouponAsync(unknown, staff.Call()), "NOT_FOUND");
    }

    [Fact]
    public async Task Total_limit_below_uses_is_VALIDATION_FAILED()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var staff = TestCaller.Staff("catalog_manager");
        var coupon = await CreateAsync(t, "Save-10", staff);
        await using (var db = t.NewDb())
            await db.Database.ExecuteSqlAsync($"UPDATE coupons SET uses_count = 3", Ct);
        var request = UpdateOf(coupon);
        request.TotalLimit = 2;

        var info = await Rpc.FailsAsync(t.Rpc.Staff.UpdateCouponAsync(request, staff.Call()), "VALIDATION_FAILED");

        Assert.Equal("total_limit", info.Metadata["field"]);
        await using (var db = t.NewDb())
        {
            var row = await db.Coupons.SingleAsync(Ct);
            Assert.Equal((0L, null), (row.Version, row.TotalLimit));
        }
        Assert.Equal(0, await AuditCountAsync(t, "order.update-coupon"));
        request.TotalLimit = 3; // equal to the uses is fine
        var ok = await t.Rpc.Staff.UpdateCouponAsync(request, staff.Call());
        Assert.Equal((3, 3, 1L), (ok.TotalLimit, ok.UsesCount, ok.Version));
    }

    [Fact]
    public async Task Deactivate_and_list()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var staff = TestCaller.Staff("catalog_manager");
        var codes = new[] { "AAA-1", "BBB-2", "CCC-3" };
        var coupons = new List<ApiCoupon>();
        foreach (var code in codes)
        {
            coupons.Add(await CreateAsync(t, code, staff));
            t.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        using var deactivate = await t.SendAsync(HttpMethod.Post, $"/api/admin/coupons/{coupons[1].Id}/deactivate", new { version = 0 }, staff);
        var off = await deactivate.OkJsonAsync();
        Assert.False(off.TryGetProperty("active", out var active) && active.GetBoolean());
        Assert.Equal("1", off.GetProperty("version").GetString());
        using (var again = await t.SendAsync(HttpMethod.Post, $"/api/admin/coupons/{coupons[1].Id}/deactivate", new { version = 1 }, staff)) // already inactive: versioned, no error
            Assert.Equal("2", (await again.OkJsonAsync()).GetProperty("version").GetString());
        using (var stale = await t.SendAsync(HttpMethod.Post, $"/api/admin/coupons/{coupons[1].Id}/deactivate", new { version = 0 }, staff))
            await stale.ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        using (var missing = await t.SendAsync(HttpMethod.Post, $"/api/admin/coupons/{NewId()}/deactivate", new { version = 0 }, staff))
            await missing.ExpectErrorAsync(404, "NOT_FOUND");
        Assert.Equal(2, await AuditCountAsync(t, "order.deactivate-coupon"));

        static string[] Codes(System.Text.Json.JsonElement list) => list.TryGetProperty("items", out var i) ? [.. i.EnumerateArray().Select(c => c.GetProperty("code").GetString()!)] : [];
        using (var all = await t.SendAsync(HttpMethod.Get, "/api/admin/coupons", caller: staff))
            Assert.Equal(["CCC-3", "BBB-2", "AAA-1"], Codes(await all.OkJsonAsync()));
        using (var onlyActive = await t.SendAsync(HttpMethod.Get, "/api/admin/coupons?active=true", caller: staff))
            Assert.Equal(["CCC-3", "AAA-1"], Codes(await onlyActive.OkJsonAsync()));
        using (var onlyInactive = await t.SendAsync(HttpMethod.Get, "/api/admin/coupons?active=false", caller: staff))
            Assert.Equal(["BBB-2"], Codes(await onlyInactive.OkJsonAsync()));
        using (var page = await t.SendAsync(HttpMethod.Get, "/api/admin/coupons?page=2&pageSize=2", caller: staff))
            Assert.Equal(["AAA-1"], Codes(await page.OkJsonAsync()));
        using (var bad = await t.SendAsync(HttpMethod.Get, "/api/admin/coupons?pageSize=101", caller: staff))
            await bad.ExpectValidationAsync("page_size");
    }

    [Fact]
    public async Task Express_fee_is_admin_only_and_versioned()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var admin = TestCaller.Staff("admin");

        var seeded = await t.Rpc.Staff.GetCheckoutSettingsAsync(new GetCheckoutSettingsRequest(), admin.Call());
        Assert.Equal((1500L, 0L, ""), (seeded.ExpressFeeCents, seeded.Version, seeded.UpdatedBy));

        t.Clock.Advance(TimeSpan.FromHours(1));
        using (var set = await t.SendAsync(HttpMethod.Put, "/api/admin/settings/checkout/express-fee", new { expressFeeCents = 2000, version = 0 }, admin))
        {
            var settings = await set.OkJsonAsync();
            Assert.Equal(("2000", "1", admin.Id.ToString("D")), (settings.GetProperty("expressFeeCents").GetString(), settings.GetProperty("version").GetString(), settings.GetProperty("updatedBy").GetString()));
        }
        var read = await t.Rpc.Staff.GetCheckoutSettingsAsync(new GetCheckoutSettingsRequest(), admin.Call());
        Assert.Equal((2000L, t.Clock.GetUtcNow()), (read.ExpressFeeCents, read.UpdatedAt.ToDateTimeOffset()));
        var audit = Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal(("order.set-express-fee", admin.Id), (audit.Action, audit.ActorId));

        await Rpc.FailsAsync(t.Rpc.Staff.SetExpressFeeAsync(new SetExpressFeeRequest { ExpressFeeCents = 3000, Version = 0 }, admin.Call()), "CONCURRENCY_CONFLICT");
        var negative = await Rpc.FailsAsync(t.Rpc.Staff.SetExpressFeeAsync(new SetExpressFeeRequest { ExpressFeeCents = -1, Version = 1 }, admin.Call()), "VALIDATION_FAILED");
        Assert.Equal("express_fee_cents", negative.Metadata["field"]);
        Assert.Equal(0, (await t.Rpc.Staff.SetExpressFeeAsync(new SetExpressFeeRequest { ExpressFeeCents = 0, Version = 1 }, admin.Call())).ExpressFeeCents); // free express is allowed
        foreach (var role in new[] { "support", "catalog_manager", "warehouse" })
        {
            var other = TestCaller.Staff(role);
            await Rpc.FailsAsync(t.Rpc.Staff.SetExpressFeeAsync(new SetExpressFeeRequest { ExpressFeeCents = -1, Version = 99 }, other.Call()), "ROLE_NOT_ALLOWED"); // before validation
            await Rpc.FailsAsync(t.Rpc.Staff.GetCheckoutSettingsAsync(new GetCheckoutSettingsRequest(), other.Call()), "ROLE_NOT_ALLOWED");
        }
        Assert.Equal(2, await AuditCountAsync(t, "order.set-express-fee"));
    }

    [Theory, InlineData("support"), InlineData("warehouse")]
    public async Task Only_catalog_managers_and_admins_edit_coupons(string role)
    {
        await using var t = await TestOrder.StartAsync(containers);
        var coupon = await CreateAsync(t, "Save-10", TestCaller.Staff("admin"));
        foreach (var caller in new[] { TestCaller.Staff(role), TestCaller.Customer() })
        {
            // Empty requests would fail validation: the role check comes first (plan R7).
            var o = caller.Call();
            var s = t.Rpc.Staff;
            await Rpc.FailsAsync(s.ListCouponsAsync(new ListCouponsRequest(), o), "ROLE_NOT_ALLOWED");
            await Rpc.FailsAsync(s.CreateCouponAsync(new CreateCouponRequest(), o), "ROLE_NOT_ALLOWED");
            await Rpc.FailsAsync(s.UpdateCouponAsync(new UpdateCouponRequest { CouponId = coupon.Id }, o), "ROLE_NOT_ALLOWED");
            await Rpc.FailsAsync(s.DeactivateCouponAsync(new DeactivateCouponRequest { CouponId = coupon.Id }, o), "ROLE_NOT_ALLOWED");
        }
        Assert.Equal((0L, true), await DbStateAsync(t));
        Assert.Single(await Outbox.AuditAsync(t)); // only the admin's create

        static async Task<(long, bool)> DbStateAsync(TestOrder t)
        {
            await using var db = t.NewDb();
            var row = await db.Coupons.SingleAsync(Ct);
            return (row.Version, row.Active);
        }
    }
}
