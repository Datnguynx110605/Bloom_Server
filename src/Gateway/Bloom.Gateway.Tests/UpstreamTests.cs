using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Bloom.Gateway.Tests;

public class UpstreamTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Default_route_times_out_after_5s_with_504()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/x");
        request.Headers.Add("x-test-delay-ms", "8000");

        var stopwatch = Stopwatch.StartNew();
        using var response = await gateway.Client.SendAsync(request, Ct);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        await AssertErrorAsync(response, 4, "UPSTREAM_TIMEOUT");
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.5, 6.5);
    }

    [Fact]
    public async Task Route_timeout_override_applies()
    {
        await using var gateway = await TestGateway.StartAsync(containers, o => o.Config["ReverseProxy:Routes:webhook-stripe:Timeout"] = "00:00:00.5");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe");
        request.Headers.Add("x-test-delay-ms", "3000");

        var stopwatch = Stopwatch.StartNew();
        using var response = await gateway.Client.SendAsync(request, Ct);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 0, 2);
    }

    [Fact]
    public async Task Upstream_5xx_passes_through_untouched_once()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/x");
        request.Headers.Add("x-test-status", "500");

        using var response = await gateway.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("boom", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Unreachable_upstream_is_503_UPSTREAM_UNAVAILABLE()
    {
        await using var gateway = await TestGateway.StartAsync(containers,
            o => o.Config["ReverseProxy:Clusters:catalog:Destinations:d1:Address"] = "http://127.0.0.1:1/");

        using var response = await gateway.Client.GetAsync("/api/catalog/x", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertErrorAsync(response, 14, "UPSTREAM_UNAVAILABLE");
    }

    [Theory]
    [InlineData("/api/auth/sign-in", 1048576)]
    [InlineData("/webhooks/stripe", 262144)]
    [InlineData("/webhooks/shippo/s3cret", 65536)]
    public async Task Body_limit_per_route(string path, int limit)
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        using var atLimit = await gateway.Client.PostAsync(path, new ByteArrayContent(new byte[limit]), Ct);
        using var overLimit = await gateway.Client.PostAsync(path, new ByteArrayContent(new byte[limit + 1]), Ct);

        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        using var echo = JsonDocument.Parse(await atLimit.Content.ReadAsStringAsync(Ct));
        Assert.Equal(limit, echo.RootElement.GetProperty("bodyLength").GetInt32());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimit.StatusCode);
        await AssertErrorAsync(overLimit, 3, "PAYLOAD_TOO_LARGE");
    }

    [Fact]
    public async Task Chunked_body_over_limit_is_413()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/shippo/s") { Content = new StreamContent(new MemoryStream(new byte[65537])) };
        request.Headers.TransferEncodingChunked = true; // no Content-Length

        using var response = await gateway.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertErrorAsync(response, 3, "PAYLOAD_TOO_LARGE");
    }

    [Fact]
    public async Task Headers_over_32KB_get_431()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/x");
        request.Headers.Add("x-big", new string('a', 33000));

        using var response = await gateway.Client.SendAsync(request, Ct);

        Assert.Equal((HttpStatusCode)431, response.StatusCode);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, int code, string reason)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(code, body.RootElement.GetProperty("code").GetInt32());
        var detail = Assert.Single(body.RootElement.GetProperty("details").EnumerateArray());
        Assert.Equal(reason, detail.GetProperty("reason").GetString());
    }
}
