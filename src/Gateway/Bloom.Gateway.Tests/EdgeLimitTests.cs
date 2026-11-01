using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using static Bloom.Gateway.Tests.Http;

namespace Bloom.Gateway.Tests;

public class EdgeLimitTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Rate_limits_match_LLD_4_2()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();

        var policies = RateLimitPolicy.Bind(config);

        RateLimitPolicy[] expected =
        [
            new("global", Algorithm.FixedWindow, LimitKey.Ip, 300, 60, 0, 0),
            new("auth", Algorithm.SlidingWindow, LimitKey.Ip, 10, 60, 0, 0),
            new("refresh", Algorithm.SlidingWindow, LimitKey.Ip, 20, 60, 0, 0),
            new("user", Algorithm.TokenBucket, LimitKey.User, 0, 0, 60, 120),
            new("checkout", Algorithm.TokenBucket, LimitKey.User, 0, 0, 3, 6),
            new("wr-join", Algorithm.TokenBucket, LimitKey.User, 0, 0, 3, 5),
            new("wr-status", Algorithm.TokenBucket, LimitKey.User, 0, 0, 5, 30),
            new("webhook", Algorithm.FixedWindow, LimitKey.Ip, 600, 60, 0, 0),
        ];
        Assert.Equal(expected.ToDictionary(p => p.Name), policies);
    }

    [Fact]
    public async Task G03_token_issued_before_revocation_is_TOKEN_REVOKED()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var metrics = new MetricCapture(gateway.Services);
        var sub = Guid.NewGuid();
        var token = TestTokens.Issue(gateway.DefaultKey, sub: sub);
        var iat = gateway.Clock.GetUtcNow().ToUnixTimeSeconds();
        await containers.Redis.GetDatabase().StringSetAsync($"rev:{sub:D}", iat * 1000 + 1);

        using var response = await SendAsync(gateway, "GET", "/api/cart", token);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_REVOKED");
        Assert.Equal(0, gateway.Upstream.RequestCount);
        Assert.Empty(Keys($"fw:global:{gateway.ClientIp}:*"));
        Assert.Equal(1, metrics.Sum("gateway_auth_failures_total", ("reason", "TOKEN_REVOKED")));
    }

    [Fact]
    public async Task Revocation_in_the_same_millisecond_as_iat_does_not_revoke()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var sub = Guid.NewGuid();
        var token = TestTokens.Issue(gateway.DefaultKey, sub: sub);
        await containers.Redis.GetDatabase().StringSetAsync($"rev:{sub:D}", gateway.Clock.GetUtcNow().ToUnixTimeSeconds() * 1000);

        using var response = await SendAsync(gateway, "GET", "/api/cart", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task G07_11th_sign_in_within_60s_is_429_with_Retry_After()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var start = StartOfNextWindow(gateway);
        gateway.Clock.SetUtcNow(start);

        for (var i = 0; i < 10; i++)
        {
            using var ok = await SendAsync(gateway, "POST", "/api/auth/sign-in");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            gateway.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        using var response = await SendAsync(gateway, "POST", "/api/auth/sign-in");

        await AssertErrorAsync(response, HttpStatusCode.TooManyRequests, 8, "RATE_LIMITED");
        Assert.Equal("50", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Fact]
    public async Task G07b_301st_request_in_a_window_is_429()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway) + TimeSpan.FromSeconds(10));

        for (var i = 0; i < 300; i++)
        {
            using var ok = await SendAsync(gateway, "GET", "/api/catalog/x");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var response = await SendAsync(gateway, "GET", "/api/catalog/x");

        await AssertErrorAsync(response, HttpStatusCode.TooManyRequests, 8, "RATE_LIMITED");
        Assert.Equal("50", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Fact]
    public async Task G07c_4th_checkout_in_a_second_is_429()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway));
        var token = TestTokens.Issue(gateway.DefaultKey);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await SendAsync(gateway, "POST", "/api/checkout", token);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var response = await SendAsync(gateway, "POST", "/api/checkout", token);
        gateway.Clock.Advance(TimeSpan.FromSeconds(10));
        using var later = await SendAsync(gateway, "POST", "/api/checkout", token);

        await AssertErrorAsync(response, HttpStatusCode.TooManyRequests, 8, "RATE_LIMITED");
        Assert.Equal("10", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task Sliding_window_has_no_boundary_burst()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway) + TimeSpan.FromSeconds(30)); // 30 s past a fixed-window boundary

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++) // 30 s, 33 s … 60 s: the 11th lands in the next fixed window
        {
            using var response = await SendAsync(gateway, "POST", "/api/auth/sign-in");
            statuses.Add(response.StatusCode);
            gateway.Clock.Advance(TimeSpan.FromSeconds(3));
        }

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    [Fact]
    public async Task Signed_in_burst_of_30_is_allowed()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway));
        var token = TestTokens.Issue(gateway.DefaultKey);

        for (var i = 0; i < 30; i++)
        {
            using var response = await SendAsync(gateway, "GET", "/api/catalog/x", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Waiting_room_status_every_5s_is_never_limited()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway));
        var token = TestTokens.Issue(gateway.DefaultKey);

        for (var i = 0; i < 120; i++)
        {
            using var response = await SendAsync(gateway, "GET", "/api/launches/l1/waiting-room", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            gateway.Clock.Advance(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Webhooks_skip_global_and_use_webhook_limit()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway));

        for (var i = 0; i < 600; i++)
        {
            using var ok = await SendAsync(gateway, "POST", "/webhooks/stripe");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var response = await SendAsync(gateway, "POST", "/webhooks/stripe");

        await AssertErrorAsync(response, HttpStatusCode.TooManyRequests, 8, "RATE_LIMITED");
        Assert.Empty(Keys($"fw:global:{gateway.ClientIp}:*"));
        Assert.Single(Keys($"fw:webhook:{gateway.ClientIp}:*"));
    }

    [Fact]
    public async Task Global_denial_leaves_route_policy_untouched()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway));
        for (var i = 0; i < 300; i++)
            (await SendAsync(gateway, "GET", "/api/catalog/x")).Dispose();

        using var response = await SendAsync(gateway, "POST", "/api/auth/sign-in");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(await containers.Redis.GetDatabase().KeyExistsAsync($"sw:auth:{gateway.ClientIp}"));
    }

    [Fact]
    public async Task Route_denial_still_counts_global()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway));
        for (var i = 0; i < 11; i++)
            (await SendAsync(gateway, "POST", "/api/auth/sign-in")).Dispose();

        var count = await containers.Redis.GetDatabase().StringGetAsync(GlobalKey(gateway));

        Assert.Equal(11, (int)count);
    }

    [Fact]
    public async Task Redis_keys_and_ttls_follow_4_1()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway));
        var sub = Guid.NewGuid();
        var token = TestTokens.Issue(gateway.DefaultKey, sub: sub);
        var db = containers.Redis.GetDatabase();

        (await SendAsync(gateway, "GET", "/api/cart", token)).Dispose();
        (await SendAsync(gateway, "POST", "/api/checkout", token)).Dispose();
        (await SendAsync(gateway, "POST", "/api/auth/sign-in")).Dispose();

        var global = (await db.KeyTimeToLiveAsync(GlobalKey(gateway))).GetValueOrDefault().TotalMilliseconds;
        Assert.InRange(global, 60001, 65000);
        Assert.InRange((await db.KeyTimeToLiveAsync($"sw:auth:{gateway.ClientIp}")).GetValueOrDefault().TotalMilliseconds, 1, 60000);
        Assert.InRange((await db.KeyTimeToLiveAsync($"tb:user:{sub:D}")).GetValueOrDefault().TotalMilliseconds, 1, 60000);
        Assert.InRange((await db.KeyTimeToLiveAsync($"tb:checkout:{sub:D}")).GetValueOrDefault().TotalMilliseconds, 1, 60000);
        Assert.Equal(["tokens", "ts"], (await db.HashGetAllAsync($"tb:user:{sub:D}")).Select(e => (string)e.Name!).Order());
    }

    [Fact]
    public async Task Rate_limited_metric_by_policy()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var metrics = new MetricCapture(gateway.Services);
        gateway.Clock.SetUtcNow(StartOfNextWindow(gateway));

        for (var i = 0; i < 11; i++)
            (await SendAsync(gateway, "POST", "/api/auth/sign-in")).Dispose();

        Assert.Equal(1, metrics.Sum("gateway_rate_limited_total", ("policy", "auth")));
    }

    [Fact]
    public async Task G08_spoofed_X_Forwarded_For_is_ignored()
    {
        // (a) The trusted ingress (127.0.0.1) appended the real client; the spoofed first hop is ignored.
        await using (var gateway = await TestGateway.StartAsync(containers))
        {
            using var client = new HttpClient { BaseAddress = gateway.Address, DefaultRequestHeaders = { { "X-Forwarded-For", $"6.6.6.6, {gateway.ClientIp}" } } };

            (await client.GetAsync("/api/catalog/x", Ct)).Dispose();

            Assert.Single(Keys($"fw:global:{gateway.ClientIp}:*"));
            Assert.Empty(Keys("fw:global:6.6.6.6:*"));
        }

        // (b) No trusted proxy: the header is not read at all.
        await using (var gateway = await TestGateway.StartAsync(containers, o => o.Config.Remove("Gateway:TrustedProxies:0")))
        {
            using var client = new HttpClient { BaseAddress = gateway.Address, DefaultRequestHeaders = { { "X-Forwarded-For", "6.6.6.7" } } };

            (await client.GetAsync("/api/catalog/x", Ct)).Dispose();

            Assert.Empty(Keys("fw:global:6.6.6.7:*"));
            Assert.NotEmpty(Keys("fw:global:127.0.0.1:*"));
        }
    }

    /// <summary>The next 60 s fixed-window boundary after the gateway's clock; the clock can only move forward.</summary>
    private static DateTimeOffset StartOfNextWindow(TestGateway gateway) =>
        DateTimeOffset.FromUnixTimeMilliseconds((gateway.Clock.GetUtcNow().ToUnixTimeMilliseconds() / 60000 + 1) * 60000);

    private static string GlobalKey(TestGateway gateway) => $"fw:global:{gateway.ClientIp}:{gateway.Clock.GetUtcNow().ToUnixTimeMilliseconds() / 60000}";

    private List<string> Keys(string pattern) =>
        containers.Redis.GetServers().SelectMany(s => s.Keys(pattern: pattern)).Select(k => k.ToString()).ToList();
}
