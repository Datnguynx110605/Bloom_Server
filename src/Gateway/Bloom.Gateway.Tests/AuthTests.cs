using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Gateway.Tests;

public class AuthTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> BadTokens() =>
    [
        "expired", "wrong-issuer", "wrong-audience", "rs256", "iat-in-future", "sub-missing", "sub-not-a-guid",
        "typ-robot", "staff-without-role", "auth-time-missing", "garbage", "unknown-key", "forged-signature", "tampered-payload",
    ];

    [Fact]
    public async Task G01_upstream_sees_token_identity()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var sub = Guid.NewGuid();

        using var customer = Get("/api/cart", TestTokens.Issue(gateway.DefaultKey, sub: sub, edit: c => c["auth_time"] = 1730000000L));
        customer.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        customer.Headers.Add("x-bloom-role", "admin");
        var seen = await EchoHeadersAsync(gateway, customer);

        Assert.Equal(sub.ToString("D"), seen["x-bloom-user-id"]);
        Assert.Equal("customer", seen["x-bloom-user-type"]);
        Assert.Equal("1730000000", seen["x-bloom-auth-time"]);
        Assert.DoesNotContain("x-bloom-role", seen.Keys);
        Assert.DoesNotContain("authorization", seen.Keys);

        using var staff = Get("/api/admin/orders/x", TestTokens.Issue(gateway.DefaultKey, "staff", "admin"));
        seen = await EchoHeadersAsync(gateway, staff);

        Assert.Equal("staff", seen["x-bloom-user-type"]);
        Assert.Equal("admin", seen["x-bloom-role"]);
    }

    [Fact]
    public async Task G02_HS256_token_is_TOKEN_INVALID()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Get("/api/cart", TestTokens.IssueHs256(gateway.DefaultKey)), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Alg_none_token_is_TOKEN_INVALID()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Get("/api/cart", TestTokens.IssueUnsigned(gateway.DefaultKey)), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
    }

    [Fact]
    public async Task Missing_token_is_TOKEN_MISSING()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var basic = new HttpRequestMessage(HttpMethod.Get, "/api/cart") { Headers = { Authorization = new("Basic", "x") } };

        using var none = await gateway.Client.GetAsync("/api/cart", Ct);
        using var notBearer = await gateway.Client.SendAsync(basic, Ct);

        await AssertErrorAsync(none, HttpStatusCode.Unauthorized, 16, "TOKEN_MISSING");
        await AssertErrorAsync(notBearer, HttpStatusCode.Unauthorized, 16, "TOKEN_MISSING");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Theory, MemberData(nameof(BadTokens))]
    public async Task Bad_tokens_are_TOKEN_INVALID(string name)
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var key = gateway.DefaultKey;
        var now = gateway.Clock.GetUtcNow();
        var token = name switch
        {
            "expired" => TestTokens.Issue(key, iat: now - TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(40)),
            "wrong-issuer" => TestTokens.Issue(key, edit: c => c["iss"] = "someone-else"),
            "wrong-audience" => TestTokens.Issue(key, edit: c => c["aud"] = "other"),
            "rs256" => TestTokens.IssueRs256(key),
            "iat-in-future" => TestTokens.Issue(key, iat: now + TimeSpan.FromSeconds(60)),
            "sub-missing" => TestTokens.Issue(key, edit: c => c.Remove("sub")),
            "sub-not-a-guid" => TestTokens.Issue(key, sub: null, edit: c => c["sub"] = "abc"),
            "typ-robot" => TestTokens.Issue(key, "robot"),
            "staff-without-role" => TestTokens.Issue(key, "staff"),
            "auth-time-missing" => TestTokens.Issue(key, edit: c => c.Remove("auth_time")),
            "garbage" => "garbage",
            "unknown-key" => TestTokens.Issue(TestKeys.Create("rogue")),
            // Known kid, wrong key: only the signature check can reject these.
            "forged-signature" => TestTokens.Issue(TestKeys.Create(key.Kid)),
            "tampered-payload" => TamperedPayload(TestTokens.Issue(key), TestTokens.Issue(key, "staff", "admin")),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        var path = name == "staff-without-role" ? "/api/admin/orders/x" : "/api/cart";

        using var response = await gateway.Client.SendAsync(Get(path, token), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Expired_within_30s_skew_is_accepted()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var token = TestTokens.Issue(gateway.DefaultKey, iat: gateway.Clock.GetUtcNow() - TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(20));

        using var response = await gateway.Client.SendAsync(Get("/api/cart", token), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Customer_token_on_admin_route_is_ROLE_NOT_ALLOWED()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Get("/api/admin/orders/x", TestTokens.Issue(gateway.DefaultKey)), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, 7, "ROLE_NOT_ALLOWED");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Staff_token_on_customer_route_is_ROLE_NOT_ALLOWED()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Get("/api/cart", TestTokens.Issue(gateway.DefaultKey, "staff", "admin")), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, 7, "ROLE_NOT_ALLOWED");
    }

    [Fact]
    public async Task Staff_role_not_listed_is_ROLE_NOT_ALLOWED()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Get("/api/admin/payments/x", TestTokens.Issue(gateway.DefaultKey, "staff", "warehouse")), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, 7, "ROLE_NOT_ALLOWED");
    }

    [Fact]
    public async Task Catalog_admin_allows_support()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Get("/api/admin/catalog/x", TestTokens.Issue(gateway.DefaultKey, "staff", "support")), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Any_user_route_accepts_customer_and_staff()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var customer = await gateway.Client.SendAsync(Get("/api/me", TestTokens.Issue(gateway.DefaultKey)), Ct);
        using var staff = await gateway.Client.SendAsync(Get("/api/me", TestTokens.Issue(gateway.DefaultKey, "staff", "support")), Ct);

        Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
    }

    [Fact]
    public async Task Optional_user_route_reads_a_present_token()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var sub = Guid.NewGuid();

        var anonymous = await EchoHeadersAsync(gateway, Get("/api/catalog/x", null));
        var signedIn = await EchoHeadersAsync(gateway, Get("/api/catalog/x", TestTokens.Issue(gateway.DefaultKey, sub: sub)));
        using var bad = await gateway.Client.SendAsync(Get("/api/catalog/x", "garbage"), Ct);

        Assert.DoesNotContain("x-bloom-user-id", anonymous.Keys);
        Assert.Equal(sub.ToString("D"), signedIn["x-bloom-user-id"]);
        Assert.Equal("customer", signedIn["x-bloom-user-type"]);
        await AssertErrorAsync(bad, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
    }

    [Theory]
    [InlineData("POST", "/api/auth/sign-in")]
    [InlineData("POST", "/auth/refresh")]
    [InlineData("POST", "/webhooks/stripe")]
    [InlineData("GET", "/api/notifications/hub/negotiate")]
    public async Task Stale_token_is_ignored_on_public_routes(string method, string path)
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var expired = TestTokens.Issue(gateway.DefaultKey, iat: gateway.Clock.GetUtcNow() - TimeSpan.FromHours(1));
        using var request = Get(path, expired, method);
        request.Headers.Add("Origin", "https://bloom.test");

        var seen = await EchoHeadersAsync(gateway, request);

        Assert.DoesNotContain("x-bloom-user-id", seen.Keys);
    }

    [Fact]
    public async Task G10_unknown_kid_refetches_JWKS_once()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var rotated = TestKeys.Create("rotated", gateway.Clock);
        var later = TestKeys.Create("later", gateway.Clock);
        gateway.Jwks.SetKeys(gateway.DefaultKey, rotated);
        var before = gateway.Jwks.FetchCount;

        using var first = await gateway.Client.SendAsync(Get("/api/cart", TestTokens.Issue(rotated)), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(before + 1, gateway.Jwks.FetchCount);

        using var second = await gateway.Client.SendAsync(Get("/api/cart", TestTokens.Issue(later)), Ct); // not published yet, and inside the 30 s window

        await AssertErrorAsync(second, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
        Assert.Equal(before + 1, gateway.Jwks.FetchCount);

        gateway.Jwks.SetKeys(gateway.DefaultKey, rotated, later);
        gateway.Clock.Advance(TimeSpan.FromSeconds(30));
        using var third = await gateway.Client.SendAsync(Get("/api/cart", TestTokens.Issue(later)), Ct);

        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(before + 2, gateway.Jwks.FetchCount);
    }

    [Fact]
    public async Task Concurrent_unknown_kid_requests_fetch_once()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var rotated = TestKeys.Create("rotated", gateway.Clock);
        gateway.Jwks.SetKeys(gateway.DefaultKey, rotated);
        var before = gateway.Jwks.FetchCount;

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var response = await gateway.Client.SendAsync(Get("/api/cart", TestTokens.Issue(rotated)), Ct);
            return response.StatusCode;
        }));

        Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(before + 1, gateway.Jwks.FetchCount);
    }

    [Fact]
    public async Task JWKS_refreshes_every_10_minutes()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var metrics = new MetricCapture(gateway.Services);
        var before = gateway.Jwks.FetchCount;

        gateway.Clock.Advance(TimeSpan.FromMinutes(10));

        await Poll.UntilAsync(() => gateway.Jwks.FetchCount == before + 1, 5, "the periodic refresh");
        await Poll.UntilAsync(() => metrics.Sum("jwks_refresh_total", ("result", "success")) == 1, 5, "the refresh is counted");
    }

    [Fact]
    public async Task Readyz_waits_for_JWKS()
    {
        await using var gateway = await TestGateway.StartAsync(containers, o => { o.JwksAvailable = false; o.WaitForReady = false; });
        using var metrics = new MetricCapture(gateway.Services);

        using var notReady = await gateway.Client.GetAsync("/readyz", Ct);
        using var alive = await gateway.Client.GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        await Poll.UntilAsync(() =>
        {
            gateway.Clock.Advance(TimeSpan.FromSeconds(1)); // the retry delay runs on the fake clock
            return metrics.Sum("jwks_refresh_total", ("result", "error")) >= 1;
        }, 5, "a failed fetch is counted");

        gateway.Jwks.Available = true;
        await Poll.UntilAsync(async () =>
        {
            gateway.Clock.Advance(TimeSpan.FromSeconds(1));
            return (await gateway.Client.GetAsync("/readyz", Ct)).StatusCode == HttpStatusCode.OK;
        }, 5, "/readyz turns 200");
    }

    [Fact]
    public async Task Auth_failure_metric_by_reason()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var metrics = new MetricCapture(gateway.Services);

        (await gateway.Client.GetAsync("/api/cart", Ct)).Dispose();
        (await gateway.Client.SendAsync(Get("/api/cart", "garbage"), Ct)).Dispose();

        await Poll.UntilAsync(() => metrics.Sum("gateway_auth_failures_total", ("reason", "TOKEN_MISSING")) == 1, 5, "TOKEN_MISSING counted");
        Assert.Equal(1, metrics.Sum("gateway_auth_failures_total", ("reason", "TOKEN_INVALID")));
    }

    [Fact]
    public async Task Authorization_and_cookie_are_never_logged()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var good = TestTokens.Issue(gateway.DefaultKey);
        var bad = TestTokens.Issue(gateway.DefaultKey, iat: gateway.Clock.GetUtcNow() - TimeSpan.FromHours(1));

        foreach (var (path, token) in new[] { ("/api/cart", good), ("/api/cart", bad), ("/api/admin/orders/x", good) })
        {
            using var request = Get(path, token);
            request.Headers.Add("Cookie", "bloom_rt=s3cr3t");
            (await gateway.Client.SendAsync(request, Ct)).Dispose();
        }

        var entries = gateway.Logs.Entries;
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            var text = entry.Message + string.Concat(entry.State.Select(s => s.Key + "=" + s.Value));
            Assert.DoesNotContain(good, text);
            Assert.DoesNotContain(bad, text);
            Assert.DoesNotContain("s3cr3t", text);
        }
    }

    /// <summary>The header and signature of <paramref name="signed"/> with the payload of <paramref name="other"/>.</summary>
    private static string TamperedPayload(string signed, string other)
    {
        var (a, b) = (signed.Split('.'), other.Split('.'));
        return $"{a[0]}.{b[1]}.{a[2]}";
    }

    private static HttpRequestMessage Get(string path, string? token, string method = "GET") =>
        new(new HttpMethod(method), path) { Headers = { Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token) } };

    /// <summary>Sends the request and returns the headers the upstream received, lower-cased.</summary>
    private static async Task<Dictionary<string, string>> EchoHeadersAsync(TestGateway gateway, HttpRequestMessage request)
    {
        using var response = await gateway.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, int code, string reason)
    {
        Assert.Equal(status, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetInt32());
        var detail = Assert.Single(body.RootElement.GetProperty("details").EnumerateArray());
        Assert.Equal(reason, detail.GetProperty("reason").GetString());
    }
}
