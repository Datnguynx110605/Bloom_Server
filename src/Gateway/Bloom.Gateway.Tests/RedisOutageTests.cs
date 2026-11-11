using System.Net;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Testcontainers.Redis;
using static Bloom.Gateway.Tests.Http;

namespace Bloom.Gateway.Tests;

public class RedisOutageTests(Containers containers)
{
    // Nothing listens on port 1; the short connect timeout keeps the first connection attempt quick.
    private const string Outage = "127.0.0.1:1,abortConnect=false,connectTimeout=200";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task G05_staff_request_during_Redis_outage_is_503()
    {
        await using var gateway = await StartDuringOutageAsync();
        var token = TestTokens.Issue(gateway.DefaultKey, typ: "staff", role: "admin");

        using var response = await SendAsync(gateway, "GET", "/api/admin/orders/x", token);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, 14, "AUTH_STORE_UNAVAILABLE");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Staff_token_on_optional_user_route_during_outage_is_503()
    {
        await using var gateway = await StartDuringOutageAsync();
        var token = TestTokens.Issue(gateway.DefaultKey, typ: "staff", role: "admin");

        using var response = await SendAsync(gateway, "GET", "/api/catalog/x", token);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, 14, "AUTH_STORE_UNAVAILABLE");
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task G06_customer_request_during_outage_is_served_with_in_memory_limits()
    {
        await using var gateway = await StartDuringOutageAsync();
        var token = TestTokens.Issue(gateway.DefaultKey);

        using var cart = await SendAsync(gateway, "GET", "/api/cart", token);
        Assert.Equal(HttpStatusCode.OK, cart.StatusCode);
        for (var i = 0; i < 3; i++)
        {
            using var ok = await SendAsync(gateway, "POST", "/api/checkout", token);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var response = await SendAsync(gateway, "POST", "/api/checkout", token);

        await AssertErrorAsync(response, HttpStatusCode.TooManyRequests, 8, "RATE_LIMITED");
        Assert.True(int.Parse(response.Headers.GetValues("Retry-After").Single()) >= 1);
    }

    [Fact]
    public async Task Anonymous_during_outage_uses_in_memory_limits()
    {
        await using var gateway = await StartDuringOutageAsync();

        for (var i = 0; i < 10; i++)
        {
            using var ok = await SendAsync(gateway, "POST", "/api/auth/sign-in");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var response = await SendAsync(gateway, "POST", "/api/auth/sign-in");

        await AssertErrorAsync(response, HttpStatusCode.TooManyRequests, 8, "RATE_LIMITED");
    }

    [Fact]
    public async Task Invalid_token_during_outage_is_401()
    {
        await using var gateway = await StartDuringOutageAsync();

        using var response = await SendAsync(gateway, "GET", "/api/cart", TestTokens.IssueHs256(gateway.DefaultKey));

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, 16, "TOKEN_INVALID");
    }

    [Fact]
    public async Task Redis_degraded_gauge_is_1_during_outage()
    {
        await using var gateway = await StartDuringOutageAsync();
        using var metrics = new MetricCapture(gateway.Services);

        (await SendAsync(gateway, "GET", "/api/catalog/x")).Dispose();

        Assert.Equal(1, metrics.Gauge("gateway_redis_degraded"));
    }

    [Fact]
    public async Task Recovers_when_Redis_returns()
    {
        await using var redis = new RedisBuilder("redis:8.2.3").Build();
        await redis.StartAsync(Ct);
        // A paused container keeps its sockets open, so commands end by timing out; keep that short.
        var connection = $"{redis.GetConnectionString()},asyncTimeout=500";
        await using var gateway = await TestGateway.StartAsync(containers, o => o.Config["Gateway:Redis"] = connection);
        using var metrics = new MetricCapture(gateway.Services);
        await using var inspector = await ConnectionMultiplexer.ConnectAsync(connection);
        var token = TestTokens.Issue(gateway.DefaultKey, typ: "staff", role: "admin");
        using (var healthy = await SendAsync(gateway, "GET", "/api/admin/orders/x", token))
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal(0, metrics.Gauge("gateway_redis_degraded"));

        await redis.PauseAsync(Ct);
        using (var down = await SendAsync(gateway, "GET", "/api/admin/orders/x", token))
            await AssertErrorAsync(down, HttpStatusCode.ServiceUnavailable, 14, "AUTH_STORE_UNAVAILABLE");
        Assert.Equal(1, metrics.Gauge("gateway_redis_degraded"));

        await redis.UnpauseAsync(Ct);
        await Poll.UntilAsync(async () =>
        {
            using var response = await SendAsync(gateway, "GET", "/api/admin/orders/x", token);
            return response.StatusCode == HttpStatusCode.OK;
        }, 10, "staff request is 200 again");
        Assert.Equal(0, metrics.Gauge("gateway_redis_degraded"));
        var server = inspector.GetServers().Single();
        Assert.NotEmpty(server.Keys(pattern: $"fw:global:{gateway.ClientIp}:*"));
    }

    [Fact]
    public async Task Degraded_gauge_updates_while_idle()
    {
        await using var gateway = await StartDuringOutageAsync();
        using var metrics = new MetricCapture(gateway.Services);
        Assert.Equal(0, metrics.Gauge("gateway_redis_degraded")); // no request has noticed the outage yet

        gateway.Clock.Advance(TimeSpan.FromMinutes(1));

        await Poll.UntilAsync(() => metrics.Gauge("gateway_redis_degraded") == 1, 10, "the gauge is 1 with no requests");
    }

    [Fact]
    public async Task Degraded_logs_once_on_enter_and_leave()
    {
        await using var redis = new RedisBuilder("redis:8.2.3").Build();
        await redis.StartAsync(Ct);
        var connection = $"{redis.GetConnectionString()},asyncTimeout=500";
        await using var gateway = await TestGateway.StartAsync(containers, o => o.Config["Gateway:Redis"] = connection);
        var token = TestTokens.Issue(gateway.DefaultKey, typ: "staff", role: "admin");
        using (var healthy = await SendAsync(gateway, "GET", "/api/admin/orders/x", token))
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Empty(Degraded(gateway));

        await redis.PauseAsync(Ct);
        for (var i = 0; i < 3; i++)
            using (var down = await SendAsync(gateway, "GET", "/api/admin/orders/x", token))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal([LogLevel.Warning], Degraded(gateway).Select(e => e.Level));

        await redis.UnpauseAsync(Ct);
        await Poll.UntilAsync(async () =>
        {
            using var response = await SendAsync(gateway, "GET", "/api/admin/orders/x", token);
            return response.StatusCode == HttpStatusCode.OK;
        }, 10, "staff request is 200 again");
        for (var i = 0; i < 3; i++)
            (await SendAsync(gateway, "GET", "/api/admin/orders/x", token)).Dispose();

        Assert.Equal([LogLevel.Warning, LogLevel.Information], Degraded(gateway).Select(e => e.Level)); // one entry per transition, not per request
    }

    private static IReadOnlyList<LogCapture.Entry> Degraded(TestGateway gateway) =>
        gateway.Logs.Entries.Where(e => e.Category == typeof(GatewayMetrics).FullName).ToList();

    private Task<TestGateway> StartDuringOutageAsync() =>
        TestGateway.StartAsync(containers, o => o.Config["Gateway:Redis"] = Outage);
}
