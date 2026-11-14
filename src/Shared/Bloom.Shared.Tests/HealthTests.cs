using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bloom.Shared.Tests;

/// <summary>HLD-ADR-020: <c>/healthz</c> in both modes, <c>/readyz</c> in API mode only (started, not stopping), no dependency check.</summary>
public class HealthTests
{
    private static WebApplication Build(bool api)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapBloomHealth(api);
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(WebApplication app, string path)
    {
        using var client = app.GetTestClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task Healthz_is_200_in_both_modes_and_empty(bool api)
    {
        await using var app = Build(api);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var (status, body) = await GetAsync(app, "/healthz");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body);
    }

    [Fact]
    public async Task Readyz_is_200_once_started_and_503_once_stopping_begins()
    {
        await using var app = Build(api: true);
        await app.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "/readyz")).Status);

        // The host is started, not run: this cancels ApplicationStopping and nothing else, so the request below is deterministic.
        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await GetAsync(app, "/readyz")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(app, "/healthz")).Status); // liveness is unaffected by shutdown
    }

    [Fact]
    public async Task Readyz_is_503_before_the_host_has_started()
    {
        await using var app = Build(api: true);
        var readyz = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/readyz");
        var context = new DefaultHttpContext { RequestServices = app.Services };

        await readyz.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task Worker_mode_has_no_readyz()
    {
        await using var app = Build(api: false);
        await app.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(app, "/readyz")).Status);
    }
}
