using System.Net;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Microsoft.AspNetCore.Http;

namespace Bloom.Gateway.Tests;

[Collection("gateway-worker")] // its worker test consumes the same queue as RevocationWorkerTests
public class ShellTests(Containers containers)
{
    [Fact]
    public void TokensRevokedV1_uses_web_json_names()
    {
        var id = Guid.NewGuid();
        var message = new TokensRevokedV1(id, 1730419200000);

        var json = JsonSerializer.Serialize(message, JsonSerializerOptions.Web);

        Assert.Equal($"{{\"accountId\":\"{id}\",\"issuedBefore\":1730419200000}}", json);
        Assert.Equal(message, JsonSerializer.Deserialize<TokensRevokedV1>(json, JsonSerializerOptions.Web));
    }

    [Fact]
    public async Task Unknown_route_is_404_ROUTE_NOT_FOUND()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        var response = await gateway.Client.GetAsync("/nope", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["code", "details", "message"], body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(5, body.RootElement.GetProperty("code").GetInt32());
        var detail = Assert.Single(body.RootElement.GetProperty("details").EnumerateArray());
        Assert.Equal("type.googleapis.com/google.rpc.ErrorInfo", detail.GetProperty("@type").GetString());
        Assert.Equal("ROUTE_NOT_FOUND", detail.GetProperty("reason").GetString());
        Assert.Equal("bloom", detail.GetProperty("domain").GetString());
    }

    [Fact]
    public async Task Api_mode_serves_healthz()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        var response = await gateway.Client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Worker_mode_serves_healthz_only()
    {
        await using var gateway = await TestGateway.StartAsync(containers, o =>
        {
            o.Config["Gateway:Mode"] = "worker";
            o.WaitForReady = false; // the worker has no /readyz
        });

        Assert.Equal(HttpStatusCode.OK, (await gateway.Client.GetAsync("/healthz", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await gateway.Client.GetAsync("/api/catalog/x", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public void Unknown_mode_fails_at_build() =>
        Assert.Throws<InvalidOperationException>(() =>
            GatewayApp.Build([], b => b.Configuration["Gateway:Mode"] = "edge"));

    [Theory]
    [InlineData(1.2, "2")]
    [InlineData(0.1, "1")]
    public async Task Retry_after_is_whole_seconds_rounded_up(double seconds, string expected)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await GatewayErrors.WriteAsync(context, GatewayErrors.RateLimited, TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, context.Response.Headers.RetryAfter.ToString());
        Assert.Equal(429, context.Response.StatusCode);
    }
}
