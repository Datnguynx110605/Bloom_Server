using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

    private static async Task<HttpResponseMessage> GetAsync(WebApplication app, string path) =>
        await app.GetTestClient().GetAsync(path, TestContext.Current.CancellationToken);

    [Theory, InlineData(true), InlineData(false)]
    public async Task Healthz_is_200_in_both_modes_and_empty(bool api)
    {
        await using var app = Build(api);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await GetAsync(app, "/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Healthz_stays_200_while_stopping()
    {
        await using var app = Build(api: true);
        await app.StartAsync(TestContext.Current.CancellationToken);
        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        using var response = await GetAsync(app, "/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_200_once_started_and_503_once_stopping_begins()
    {
        await using var app = Build(api: true);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using (var started = await GetAsync(app, "/readyz"))
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        // The host is started, not run: this cancels ApplicationStopping and nothing else, so the request below is deterministic.
        app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        using var stopping = await GetAsync(app, "/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stopping.StatusCode);
    }

    [Fact]
    public async Task Worker_mode_has_no_readyz()
    {
        await using var app = Build(api: false);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await GetAsync(app, "/readyz");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
