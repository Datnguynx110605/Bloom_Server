using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy;
using static Bloom.Gateway.Tests.Http;

namespace Bloom.Gateway.Tests;

public class RoutingTests(Containers containers)
{
    // routeId, methods, path, cluster, auth, roles, policy, timeout, maxBody, checkOrigin: one row per line of the LLD 07 §5.1 table.
    public static TheoryData<string, string?, string, string, string, string?, string?, string?, string?, bool> RouteTable() => new()
    {
        { "catalog-public", "GET", "/api/catalog/{**rest}", "catalog", "public-optional-user", null, "user", "00:00:05", null, false },
        { "admin-catalog", null, "/api/admin/catalog/{**rest}", "catalog", "staff", "catalog_manager,support,admin", "user", "00:00:05", null, false },
        { "auth", null, "/api/auth/{**rest}", "identity", "public", null, "auth", "00:00:05", null, false },
        { "auth-refresh", "POST", "/auth/refresh", "identity", "none", null, "refresh", "00:00:05", null, true },
        { "me", null, "/api/me/{**rest}", "identity", "any-user", null, "user", "00:00:05", null, false },
        { "admin-staff", null, "/api/admin/staff/{**rest}", "identity", "staff", "admin", "user", "00:00:05", null, false },
        { "admin-audit", null, "/api/admin/audit/{**rest}", "identity", "staff", "admin", "user", "00:00:05", null, false },
        { "cart", null, "/api/cart/{**rest}", "order", "customer", null, "user", "00:00:05", null, false },
        { "reviews", null, "/api/reviews/{**rest}", "catalog", "customer", null, "user", "00:00:05", null, false },
        { "orders", null, "/api/orders/{**rest}", "order", "customer", null, "user", "00:00:05", null, false },
        { "checkout", "POST", "/api/checkout", "order", "customer", null, "checkout", "00:00:15", null, false },
        { "admin-orders", null, "/api/admin/orders/{**rest}", "order", "staff", "support,admin", "user", "00:00:05", null, false },
        { "admin-coupons", null, "/api/admin/coupons/{**rest}", "order", "staff", "catalog_manager,admin", "user", "00:00:05", null, false },
        { "admin-settings", null, "/api/admin/settings/{**rest}", "order", "staff", "admin", "user", "00:00:05", null, false },
        { "launch-public", "GET", "/api/launches/{id}", "launch", "public-optional-user", null, "user", "00:00:05", null, false },
        { "waiting-room-join", "POST", "/api/launches/{id}/waiting-room", "launch", "customer", null, "wr-join", "00:00:05", null, false },
        { "waiting-room-status", "GET", "/api/launches/{id}/waiting-room", "launch", "customer", null, "wr-status", "00:00:05", null, false },
        { "admin-launches", null, "/api/admin/launches/{**rest}", "launch", "staff", "catalog_manager,admin", "user", "00:00:05", null, false },
        { "admin-inventory", null, "/api/admin/inventory/{**rest}", "inventory", "staff", "warehouse,catalog_manager,support,admin", "user", "00:00:05", null, false },
        { "admin-payments", null, "/api/admin/payments/{**rest}", "payment", "staff", "support,admin", "user", "00:00:05", null, false },
        { "returns", null, "/api/returns/{**rest}", "fulfillment", "customer", null, "user", "00:00:05", null, false },
        { "warranty", null, "/api/warranty/{**rest}", "fulfillment", "customer", null, "user", "00:00:05", null, false },
        { "admin-fulfillment", null, "/api/admin/fulfillment/{**rest}", "fulfillment", "staff", "warehouse,support,admin", "user", "00:00:05", null, false },
        { "notifications", null, "/api/notifications/{**rest}", "notification", "customer", null, "user", "00:00:05", null, false },
        { "notifications-hub", null, "/api/notifications/hub/{**rest}", "notification", "none", null, null, null, null, true },
        { "dlq-catalog", null, "/api/admin/dlq/catalog/{**rest}", "catalog", "staff", "admin", "user", "00:00:05", null, false },
        { "dlq-inventory", null, "/api/admin/dlq/inventory/{**rest}", "inventory", "staff", "admin", "user", "00:00:05", null, false },
        { "dlq-launch", null, "/api/admin/dlq/launch/{**rest}", "launch", "staff", "admin", "user", "00:00:05", null, false },
        { "dlq-order", null, "/api/admin/dlq/order/{**rest}", "order", "staff", "admin", "user", "00:00:05", null, false },
        { "dlq-payment", null, "/api/admin/dlq/payment/{**rest}", "payment", "staff", "admin", "user", "00:00:05", null, false },
        { "dlq-fulfillment", null, "/api/admin/dlq/fulfillment/{**rest}", "fulfillment", "staff", "admin", "user", "00:00:05", null, false },
        { "dlq-identity", null, "/api/admin/dlq/identity/{**rest}", "identity", "staff", "admin", "user", "00:00:05", null, false },
        { "dlq-notification", null, "/api/admin/dlq/notification/{**rest}", "notification", "staff", "admin", "user", "00:00:05", null, false },
        { "webhook-stripe", "POST", "/webhooks/stripe", "payment", "none", null, "webhook", "00:00:10", "262144", false },
        { "webhook-shippo", "POST", "/webhooks/shippo/{secret}", "fulfillment", "none", null, "webhook", "00:00:10", "65536", false },
    };

    // method, path, cluster, caller (anon | customer | staff:<role>), route ID: one sample request per route.
    public static TheoryData<string, string, string, string, string> SampleRequests() => new()
    {
        { "GET", "/api/catalog/products/1", "catalog", "anon", "catalog-public" },
        { "PUT", "/api/admin/catalog/products/1", "catalog", "staff:catalog_manager", "admin-catalog" },
        { "POST", "/api/auth/login", "identity", "anon", "auth" },
        { "POST", "/auth/refresh", "identity", "anon", "auth-refresh" },
        { "GET", "/api/me/profile", "identity", "customer", "me" },
        { "GET", "/api/admin/staff/1", "identity", "staff:admin", "admin-staff" },
        { "GET", "/api/admin/audit/events", "identity", "staff:admin", "admin-audit" },
        { "GET", "/api/cart", "order", "customer", "cart" },
        { "PUT", "/api/reviews/1", "catalog", "customer", "reviews" },
        { "GET", "/api/orders/1", "order", "customer", "orders" },
        { "POST", "/api/checkout", "order", "customer", "checkout" },
        { "GET", "/api/admin/orders/1", "order", "staff:support", "admin-orders" },
        { "GET", "/api/admin/coupons/1", "order", "staff:catalog_manager", "admin-coupons" },
        { "GET", "/api/admin/settings/x", "order", "staff:admin", "admin-settings" },
        { "GET", "/api/launches/1", "launch", "anon", "launch-public" },
        { "POST", "/api/launches/1/waiting-room", "launch", "customer", "waiting-room-join" },
        { "GET", "/api/launches/1/waiting-room", "launch", "customer", "waiting-room-status" },
        { "GET", "/api/admin/launches/1", "launch", "staff:catalog_manager", "admin-launches" },
        { "GET", "/api/admin/inventory/1", "inventory", "staff:warehouse", "admin-inventory" },
        { "GET", "/api/admin/payments/1", "payment", "staff:support", "admin-payments" },
        { "GET", "/api/returns/1", "fulfillment", "customer", "returns" },
        { "GET", "/api/warranty/1", "fulfillment", "customer", "warranty" },
        { "GET", "/api/admin/fulfillment/1", "fulfillment", "staff:warehouse", "admin-fulfillment" },
        { "GET", "/api/notifications/unread", "notification", "customer", "notifications" },
        { "GET", "/api/notifications/hub/negotiate", "notification", "anon", "notifications-hub" },
        { "POST", "/api/admin/dlq/catalog/x/replay", "catalog", "staff:admin", "dlq-catalog" },
        { "POST", "/api/admin/dlq/inventory/x/replay", "inventory", "staff:admin", "dlq-inventory" },
        { "POST", "/api/admin/dlq/launch/x/replay", "launch", "staff:admin", "dlq-launch" },
        { "POST", "/api/admin/dlq/order/x/replay", "order", "staff:admin", "dlq-order" },
        { "POST", "/api/admin/dlq/payment/x/replay", "payment", "staff:admin", "dlq-payment" },
        { "POST", "/api/admin/dlq/fulfillment/x/replay", "fulfillment", "staff:admin", "dlq-fulfillment" },
        { "POST", "/api/admin/dlq/identity/x/replay", "identity", "staff:admin", "dlq-identity" },
        { "POST", "/api/admin/dlq/notification/x/replay", "notification", "staff:admin", "dlq-notification" },
        { "POST", "/webhooks/stripe", "payment", "anon", "webhook-stripe" },
        { "POST", "/webhooks/shippo/s3cret", "fulfillment", "anon", "webhook-shippo" },
    };

    [Theory, MemberData(nameof(RouteTable))]
    public async Task Route_config_matches_LLD_5_1(string routeId, string? methods, string path, string cluster,
        string auth, string? roles, string? policy, string? timeout, string? maxBody, bool checkOrigin)
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var lookup = gateway.Services.GetRequiredService<IProxyStateLookup>();

        Assert.True(lookup.TryGetRoute(routeId, out var route), routeId);
        var config = route.Config;
        Assert.Equal(path, config.Match.Path);
        Assert.Equal(methods is null ? [] : [methods], config.Match.Methods ?? []);
        Assert.Equal(cluster, config.ClusterId);
        Assert.Equal(auth, config.Metadata?["Auth"]);
        Assert.Equal(roles, config.Metadata?.GetValueOrDefault("Roles"));
        Assert.Equal(policy, config.Metadata?.GetValueOrDefault("Policy"));
        Assert.Equal(maxBody, config.Metadata?.GetValueOrDefault("MaxBodyBytes"));
        Assert.Equal(checkOrigin ? "true" : null, config.Metadata?.GetValueOrDefault("CheckOrigin"));
        Assert.Equal(timeout is null ? (TimeSpan?)null : TimeSpan.Parse(timeout), config.Timeout);
        Assert.Equal(timeout is null ? "Disable" : null, config.TimeoutPolicy);
        Assert.Equal(RouteTable().Count, lookup.GetRoutes().Count());
    }

    [Theory, MemberData(nameof(SampleRequests))]
    public async Task Requests_reach_the_route_cluster(string method, string path, string cluster, string caller, string routeId)
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var metrics = new MetricCapture(gateway.Services);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Origin", "https://bloom.test");
        if (caller == "customer")
            request.Headers.Authorization = new("Bearer", TestTokens.Issue(gateway.DefaultKey));
        else if (caller.StartsWith("staff:", StringComparison.Ordinal))
            request.Headers.Authorization = new("Bearer", TestTokens.Issue(gateway.DefaultKey, "staff", caller["staff:".Length..]));

        using var response = await gateway.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"/{cluster}{path}", (await ReadEchoAsync(response)).GetProperty("path").GetString());
        // Overlapping paths must pick the more specific route (plan P2): the hub over notifications, waiting-room over launch-public.
        await Poll.UntilAsync(() => metrics.Sum("gateway_requests_total", ("route", routeId), ("status", "200")) == 1, 5, $"the request is counted on route {routeId}");
    }

    [Theory]
    [InlineData("POST", "/api/catalog/x")]
    [InlineData("GET", "/api/checkout")]
    [InlineData("GET", "/webhooks/stripe")]
    [InlineData("GET", "/api/admin/dlq/unknown/x")]
    [InlineData("GET", "/api/launches")]
    public async Task Unlisted_method_or_path_is_404_ROUTE_NOT_FOUND(string method, string path)
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var response = await gateway.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("ROUTE_NOT_FOUND", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task G01_forged_x_bloom_headers_are_stripped()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/x");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        request.Headers.Add("x-bloom-role", "admin");
        request.Headers.Add("X-Bloom-User-Type", "staff");

        var headers = await EchoHeadersAsync(gateway, request);

        Assert.DoesNotContain(headers.Keys, k => k != "x-bloom-request-id" && k.StartsWith("x-bloom-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_id_is_a_fresh_guid()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        var ids = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/x");
            request.Headers.Add("x-bloom-request-id", "abc");
            ids.Add((await EchoHeadersAsync(gateway, request))["x-bloom-request-id"]);
        }

        Assert.All(ids, id => Assert.True(Guid.TryParseExact(id, "D", out _), id));
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task Authorization_is_not_forwarded()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/x");
        request.Headers.Add("Authorization", "Bearer secret");

        Assert.DoesNotContain("authorization", (await EchoHeadersAsync(gateway, request)).Keys);
    }

    [Fact]
    public async Task Other_client_headers_pass_through()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/x");
        request.Headers.Add("Accept-Language", "vi-VN");
        request.Headers.Add("Cookie", "a=b");

        var headers = await EchoHeadersAsync(gateway, request);

        Assert.Equal("vi-VN", headers["accept-language"]);
        Assert.Equal("a=b", headers["cookie"]);
    }

    [Fact]
    public async Task No_forwarded_headers_are_added()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        var headers = await EchoHeadersAsync(gateway, new HttpRequestMessage(HttpMethod.Get, "/api/catalog/x"));

        Assert.DoesNotContain(headers.Keys, k => k.StartsWith("x-forwarded-", StringComparison.Ordinal) || k == "x-original-for");
    }

    [Fact]
    public async Task Security_headers_on_proxied_and_error_responses()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        foreach (var (path, status) in new[] { ("/nope", HttpStatusCode.NotFound), ("/api/catalog/x", HttpStatusCode.OK) })
        {
            using var response = await gateway.Client.GetAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(status, response.StatusCode);
            Assert.Equal("max-age=31536000", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.False(response.Headers.Contains("Server"));
            Assert.False(response.Headers.Contains("X-Powered-By"));
        }
    }

    [Fact]
    public async Task Request_metrics_are_recorded()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var metrics = new MetricCapture(gateway.Services);

        (await gateway.Client.GetAsync("/api/catalog/x", TestContext.Current.CancellationToken)).Dispose();
        (await gateway.Client.GetAsync("/nope", TestContext.Current.CancellationToken)).Dispose();

        await Poll.UntilAsync(() => metrics.Sum("gateway_requests_total", ("route", "none")) == 1, 5, "the unmatched request is counted");
        await Poll.UntilAsync(() => metrics.Sum("gateway_requests_total", ("route", "catalog-public")) == 1, 5, "the proxied request is counted");
        Assert.Equal(1, metrics.Sum("gateway_requests_total", ("route", "catalog-public"), ("status", "200")));
        Assert.Equal(1, metrics.Sum("gateway_requests_total", ("route", "none"), ("status", "404")));
        Assert.Equal(1, metrics.Count("gateway_overhead_seconds", ("route", "catalog-public")));
        Assert.Equal(1, metrics.Count("gateway_upstream_seconds", ("route", "catalog-public")));
        Assert.Equal(1, metrics.Count("gateway_overhead_seconds", ("route", "none")));
        Assert.Equal(0, metrics.Count("gateway_upstream_seconds", ("route", "none")));
    }

    private static async Task<JsonElement> ReadEchoAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
}
