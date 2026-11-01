using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bloom.Identity.Data;

namespace Bloom.Identity.Tests;

public class AuditQueryTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private static AuditEntry Row(DateTimeOffset at, Guid? actor = null, string service = "identity", string action = "identity.staff-invited",
        string targetType = "staff_account", string targetId = "t-1") => new()
    {
        Id = Guid.NewGuid(), OccurredAt = at, ActorId = actor ?? Guid.NewGuid(), ActorRole = "admin", Service = service, Action = action,
        TargetType = targetType, TargetId = targetId, Details = """{"k":1}""",
    };

    private static async Task SeedAsync(TestIdentity t, params AuditEntry[] rows)
    {
        await using var db = t.NewDb();
        db.AuditEntries.AddRange(rows);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<TestCaller> AdminAsync(TestIdentity t) =>
        TestCaller.Staff((await Seed.StaffAsync(t, StaffRoles.Admin)).Account.Id, StaffRoles.Admin, t.Clock.GetUtcNow());

    private static async Task<JsonElement> QueryAsync(TestIdentity t, TestCaller caller, string query = "")
    {
        var response = await t.SendAsync(HttpMethod.Get, $"/api/admin/audit/entries{query}", caller: caller);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string[] Ids(JsonElement page) => page.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray();

    private static string Iso(DateTimeOffset at) => Uri.EscapeDataString(at.ToString("yyyy-MM-ddTHH:mm:ssZ"));

    [Fact]
    public async Task Each_filter_narrows_the_result()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var caller = await AdminAsync(t);
        var actor = Guid.NewGuid();
        var early = Row(T0);
        var late = Row(T0.AddHours(2));
        var byActor = Row(T0.AddHours(1), actor: actor);
        var byService = Row(T0.AddHours(1), service: "catalog");
        var byAction = Row(T0.AddHours(1), action: "identity.staff-role-changed");
        var byTarget = Row(T0.AddHours(1), targetType: "dead_letter", targetId: "q/9");
        await SeedAsync(t, early, late, byActor, byService, byAction, byTarget);

        async Task<string[]> Q(string query) => Ids(await QueryAsync(t, caller, query));

        Assert.Equal([late.Id.ToString()], await Q($"?from={Iso(T0.AddHours(2))}"));                        // from is inclusive
        Assert.DoesNotContain(late.Id.ToString(), await Q($"?to={Iso(T0.AddHours(2))}"));                    // to is exclusive
        Assert.Equal([early.Id.ToString()], await Q($"?to={Iso(T0.AddHours(1))}"));
        Assert.Equal(4, (await Q($"?from={Iso(T0.AddHours(1))}&to={Iso(T0.AddHours(2))}")).Length);
        Assert.Equal([byActor.Id.ToString()], await Q($"?actorId={actor}"));
        Assert.Equal([byService.Id.ToString()], await Q("?service=catalog"));
        Assert.Equal([byAction.Id.ToString()], await Q("?action=identity.staff-role-changed"));
        Assert.Equal([byTarget.Id.ToString()], await Q("?targetType=dead_letter&targetId=q%2F9"));
        Assert.Equal([byTarget.Id.ToString()], await Q($"?targetType=dead_letter&service=identity&from={Iso(T0.AddHours(1))}&to={Iso(T0.AddHours(2))}"));
        Assert.Empty(await Q($"?actorId={actor}&service=catalog"));
        Assert.Equal(6, (await Q("")).Length);
    }

    [Fact]
    public async Task Pages_cover_all_rows_newest_first()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var caller = await AdminAsync(t);
        var rows = new[] { Row(T0), Row(T0.AddSeconds(1)), Row(T0.AddSeconds(1)), Row(T0.AddSeconds(2)), Row(T0.AddSeconds(3)) }; // two share occurred_at
        await SeedAsync(t, rows);
        var expected = rows.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id.ToString(), StringComparer.Ordinal).Select(r => r.Id.ToString()).ToArray();

        var seen = new List<string>();
        var token = "";
        var pages = 0;
        do
        {
            var page = await QueryAsync(t, caller, $"?pageSize=2&pageToken={Uri.EscapeDataString(token)}");
            seen.AddRange(Ids(page));
            token = page.TryGetProperty("nextPageToken", out var next) ? next.GetString()! : "";
            pages++;
        } while (token != "");

        Assert.Equal(3, pages);
        Assert.Equal(expected, seen);
    }

    [Fact]
    public async Task Page_size_and_token_validation()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var caller = await AdminAsync(t);
        await SeedAsync(t, Enumerable.Range(0, 51).Select(i => Row(T0.AddSeconds(i))).ToArray());

        var defaults = await QueryAsync(t, caller, "?pageSize=0");
        Assert.Equal(50, Ids(defaults).Length);
        Assert.False(string.IsNullOrEmpty(defaults.GetProperty("nextPageToken").GetString()));
        Assert.Equal(51, Ids(await QueryAsync(t, caller, "?pageSize=200")).Length);

        var tooBig = await t.SendAsync(HttpMethod.Get, "/api/admin/audit/entries?pageSize=201", caller: caller);
        Assert.Equal("page_size", (await tooBig.ExpectErrorAsync(400, "VALIDATION_FAILED")).GetProperty("metadata").GetProperty("field").GetString());
        var negative = await t.SendAsync(HttpMethod.Get, "/api/admin/audit/entries?pageSize=-1", caller: caller);
        await negative.ExpectErrorAsync(400, "VALIDATION_FAILED");

        foreach (var garbage in new[] { "not-a-token", "AAAA", Convert.ToBase64String("x|y"u8.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=') })
        {
            var bad = await t.SendAsync(HttpMethod.Get, $"/api/admin/audit/entries?pageToken={Uri.EscapeDataString(garbage)}", caller: caller);
            Assert.Equal("page_token", (await bad.ExpectErrorAsync(400, "VALIDATION_FAILED")).GetProperty("metadata").GetProperty("field").GetString());
        }
    }

    [Fact]
    public async Task Audit_log_is_admin_only()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var support = TestCaller.Staff((await Seed.StaffAsync(t, StaffRoles.Support)).Account.Id, StaffRoles.Support, t.Clock.GetUtcNow());

        var response = await t.SendAsync(HttpMethod.Get, "/api/admin/audit/entries", caller: support);

        await response.ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }

    [Fact]
    public async Task Dlq_admin_is_hosted()
    {
        await using var api = await TestIdentity.StartAsync(containers);
        await using var worker = await TestIdentity.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api.ConnectionString; }); // declares the queues

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dlq/identity/queues");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        request.Headers.Add("x-bloom-user-type", "staff");
        request.Headers.Add("x-bloom-role", "Admin"); // the value Bloom.Shared checks today (plan owner item 3)
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());
        using var response = await api.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("identity.audit.dlq", await response.Content.ReadAsStringAsync(Ct));
    }
}
