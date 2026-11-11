using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using static Bloom.Gateway.Tests.Http;

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

        using var customer = Request("GET", "/api/cart", TestTokens.Issue(gateway.DefaultKey, sub: sub, edit: c => c["auth_time"] = 1730000000L));
        customer.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        customer.Headers.Add("x-bloom-role", "admin");
        var seen = await EchoHeadersAsync(gateway, customer);

        Assert.Equal(sub.ToString("D"), seen["x-bloom-user-id"]);
        Assert.Equal("customer", seen["x-bloom-user-type"]);
        Assert.Equal("1730000000", seen["x-bloom-auth-time"]);
        Assert.DoesNotContain("x-bloom-role", seen.Keys);
        Assert.DoesNotContain("authorization", seen.Keys);

        using var staff = Request("GET", "/api/admin/orders/x", TestTokens.Issue(gateway.DefaultKey, "staff", "admin"));
        seen = await EchoHeadersAsync(gateway, staff);

        Assert.Equal("staff", seen["x-bloom-user-type"]);
        Assert.Equal("admin", seen["x-bloom-role"]);
    }

    [Fact]
    public async Task G02_HS256_token_is_TOKEN_INVALID()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Request("GET", "/api/cart", TestTokens.IssueHs256(gateway.DefaultKey)), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Alg_none_token_is_TOKEN_INVALID()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Request("GET", "/api/cart", TestTokens.IssueUnsigned(gateway.DefaultKey)), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
    }

    [Fact]
    public async Task Missing_token_is_TOKEN_MISSING()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var none = await gateway.Client.GetAsync("/api/cart", Ct);

        await AssertErrorAsync(none, HttpStatusCode.Unauthorized, 16, "TOKEN_MISSING");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Theory]
    [InlineData("/api/cart")] // token required
    [InlineData("/api/catalog/products")] // public, token optional
    public async Task D13_non_bearer_authorization_is_token_invalid(string path)
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Authorization = new("Basic", "abc") } };

        using var response = await gateway.Client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
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

        using var response = await gateway.Client.SendAsync(Request("GET", path, token), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Expired_within_30s_skew_is_accepted()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var token = TestTokens.Issue(gateway.DefaultKey, iat: gateway.Clock.GetUtcNow() - TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(20));

        using var response = await gateway.Client.SendAsync(Request("GET", "/api/cart", token), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Customer_token_on_admin_route_is_ROLE_NOT_ALLOWED()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Request("GET", "/api/admin/orders/x", TestTokens.Issue(gateway.DefaultKey)), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, 7, "ROLE_NOT_ALLOWED");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Staff_token_on_customer_route_is_ROLE_NOT_ALLOWED()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Request("GET", "/api/cart", TestTokens.Issue(gateway.DefaultKey, "staff", "admin")), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, 7, "ROLE_NOT_ALLOWED");
    }

    [Fact]
    public async Task Staff_role_not_listed_is_ROLE_NOT_ALLOWED()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Request("GET", "/api/admin/payments/x", TestTokens.Issue(gateway.DefaultKey, "staff", "warehouse")), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, 7, "ROLE_NOT_ALLOWED");
    }

    [Fact]
    public async Task Review_route_refuses_anonymous_and_staff()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var anonymous = await gateway.Client.SendAsync(Request("PUT", "/api/reviews/1", null), Ct);
        using var staff = await gateway.Client.SendAsync(Request("PUT", "/api/reviews/1", TestTokens.Issue(gateway.DefaultKey, "staff", "support")), Ct);

        await AssertErrorAsync(anonymous, HttpStatusCode.Unauthorized, 16, "TOKEN_MISSING");
        await AssertErrorAsync(staff, HttpStatusCode.Forbidden, 7, "ROLE_NOT_ALLOWED");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Catalog_admin_allows_support()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(Request("GET", "/api/admin/catalog/x", TestTokens.Issue(gateway.DefaultKey, "staff", "support")), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Any_user_route_accepts_customer_and_staff()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var customer = await gateway.Client.SendAsync(Request("GET", "/api/me", TestTokens.Issue(gateway.DefaultKey)), Ct);
        using var staff = await gateway.Client.SendAsync(Request("GET", "/api/me", TestTokens.Issue(gateway.DefaultKey, "staff", "support")), Ct);

        Assert.Equal(HttpStatusCode.OK, customer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
    }

    [Fact]
    public async Task Optional_user_route_reads_a_present_token()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var sub = Guid.NewGuid();

        var anonymous = await EchoHeadersAsync(gateway, Request("GET", "/api/catalog/x", null));
        var signedIn = await EchoHeadersAsync(gateway, Request("GET", "/api/catalog/x", TestTokens.Issue(gateway.DefaultKey, sub: sub)));
        using var bad = await gateway.Client.SendAsync(Request("GET", "/api/catalog/x", "garbage"), Ct);

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
        using var request = Request(method, path, expired);
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

        using var first = await gateway.Client.SendAsync(Request("GET", "/api/cart", TestTokens.Issue(rotated)), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(before + 1, gateway.Jwks.FetchCount);

        using var second = await gateway.Client.SendAsync(Request("GET", "/api/cart", TestTokens.Issue(later)), Ct); // not published yet, and inside the 30 s window

        await AssertErrorAsync(second, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
        Assert.Equal(before + 1, gateway.Jwks.FetchCount);

        gateway.Jwks.SetKeys(gateway.DefaultKey, rotated, later);
        gateway.Clock.Advance(TimeSpan.FromSeconds(30));
        using var third = await gateway.Client.SendAsync(Request("GET", "/api/cart", TestTokens.Issue(later)), Ct);

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
            using var response = await gateway.Client.SendAsync(Request("GET", "/api/cart", TestTokens.Issue(rotated)), Ct);
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
    public async Task JWKS_without_a_usable_key_is_a_failed_fetch()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var metrics = new MetricCapture(gateway.Services);
        var token = TestTokens.Issue(gateway.DefaultKey);
        gateway.Jwks.SetKeys(); // a parseable key set with no key in it

        gateway.Clock.Advance(TimeSpan.FromMinutes(10));

        await Poll.UntilAsync(() => metrics.Sum("jwks_refresh_total", ("result", "error")) == 1, 5, "the empty key set is counted as an error");
        Assert.Equal(0, metrics.Sum("jwks_refresh_total", ("result", "success")));
        using var ready = await gateway.Client.GetAsync("/readyz", Ct);
        using var response = await SendAsync(gateway, "GET", "/api/cart", token);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the previous keys stay in use
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
        (await gateway.Client.SendAsync(Request("GET", "/api/cart", "garbage"), Ct)).Dispose();

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
            using var request = Request("GET", path, token);
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

    [Fact]
    public async Task URL_secrets_are_never_logged()
    {
        // Logs are captured through the app's own Logging configuration, so what is checked is what would be exported.
        await using var gateway = await TestGateway.StartAsync(containers);
        using var webhook = await gateway.Client.PostAsync("/webhooks/shippo/s3cret", new ByteArrayContent([]), Ct);
        using var hub = Request("GET", "/api/notifications/hub/negotiate?ticket=t1secret");
        hub.Headers.Add("Origin", "https://bloom.test");
        using var negotiate = await gateway.Client.SendAsync(hub, Ct);
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);

        var entries = gateway.Logs.Entries;
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            var text = entry.Message + string.Concat(entry.State.Select(s => s.Key + "=" + s.Value));
            Assert.DoesNotContain("s3cret", text);
            Assert.DoesNotContain("t1secret", text);
        }
    }

    /// <summary>The header and signature of <paramref name="signed"/> with the payload of <paramref name="other"/>.</summary>
    private static string TamperedPayload(string signed, string other)
    {
    [Fact]
    public async Task D15_shippo_secret_absent_from_spans()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var secret = $"s3cret-{Guid.NewGuid():N}";
        var gatewayPort = gateway.Address.Port.ToString();
        var upstreamPort = gateway.Upstream.Address.Port.ToString();
        var seen = new List<(ActivityKind Kind, string[] Tags)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Microsoft.AspNetCore" or "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                // The gateway's own request span and its outbound span: not the test client's, not the echo upstream's.
                var port = a.GetTagItem("server.port")?.ToString();
                if (a.Kind == (port == gatewayPort ? ActivityKind.Server : ActivityKind.Client) && (port == gatewayPort || port == upstreamPort))
                    lock (seen) seen.Add((a.Kind, [.. a.TagObjects.Select(t => t.Key + "=" + t.Value)]));
            },
        };
        ActivitySource.AddActivityListener(listener);

        using var response = await gateway.Client.PostAsync($"/webhooks/shippo/{secret}", new ByteArrayContent([]), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Poll.UntilAsync(() => { lock (seen) return Task.FromResult(seen.Any(s => s.Kind == ActivityKind.Server) && seen.Any(s => s.Kind == ActivityKind.Client)); }, 5, "the request spans to stop");
        lock (seen)
        {
            foreach (var tag in seen.SelectMany(s => s.Tags))
                Assert.DoesNotContain(secret, tag);
            Assert.Contains("url.path=/webhooks/shippo/***", seen.Single(s => s.Kind == ActivityKind.Server).Tags);
        }
    }

        var (a, b) = (signed.Split('.'), other.Split('.'));
        return $"{a[0]}.{b[1]}.{a[2]}";
    }
}
