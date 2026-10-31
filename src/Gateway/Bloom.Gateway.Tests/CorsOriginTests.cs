using System.Net;
using System.Text.Json;

namespace Bloom.Gateway.Tests;

public class CorsOriginTests(Containers containers)
{
    private const string Allowed = "https://bloom.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Preflight_from_allowed_origin()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = Preflight("/api/cart", Allowed);

        using var response = await gateway.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Allowed, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        Assert.Equal("600", Assert.Single(response.Headers.GetValues("Access-Control-Max-Age")));
    }

    [Fact]
    public async Task Preflight_from_foreign_origin_gets_no_CORS_headers()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = Preflight("/api/cart", "https://evil.test");

        using var response = await gateway.Client.SendAsync(request, Ct);

        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Error_responses_carry_CORS_headers()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/cart");
        request.Headers.Add("Origin", Allowed);

        using var response = await gateway.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Allowed, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task G09_refresh_from_foreign_origin_is_403()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        request.Headers.Add("Origin", "https://evil.test");

        using var response = await gateway.Client.SendAsync(request, Ct);

        await AssertOriginRejectedAsync(response);
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Refresh_without_Origin_is_403()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");

        using var response = await gateway.Client.SendAsync(request, Ct);

        await AssertOriginRejectedAsync(response);
    }

    [Fact]
    public async Task Refresh_from_allowed_origin_is_proxied_with_cookie()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        request.Headers.Add("Origin", Allowed);
        request.Headers.Add("Cookie", "bloom_rt=x");

        using var response = await gateway.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("bloom_rt=x", body.RootElement.GetProperty("headers").GetProperty("cookie").GetString());
    }

    internal static async Task AssertOriginRejectedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(7, body.RootElement.GetProperty("code").GetInt32());
        var detail = Assert.Single(body.RootElement.GetProperty("details").EnumerateArray());
        Assert.Equal("ORIGIN_NOT_ALLOWED", detail.GetProperty("reason").GetString());
    }

    private static HttpRequestMessage Preflight(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return request;
    }
}
