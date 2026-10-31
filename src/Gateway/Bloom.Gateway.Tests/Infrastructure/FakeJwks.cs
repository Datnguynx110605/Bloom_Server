using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bloom.Gateway.Tests;

/// <summary>Identity's JWKS endpoint, served from test keys; keys can be swapped to rehearse rotation.</summary>
public sealed class FakeJwks : IAsyncDisposable
{
    private readonly WebApplication _app;
    private volatile string _body = "";
    private volatile bool _available = true;
    private int _fetchCount;

    private FakeJwks(WebApplication app) => _app = app;

    public Uri Url { get; private set; } = null!;

    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <summary>False makes the endpoint answer 503.</summary>
    public bool Available { set => _available = value; }

    public static async Task<FakeJwks> StartAsync(params TestKeys[] keys)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var jwks = new FakeJwks(builder.Build());
        jwks.SetKeys(keys);
        jwks._app.MapGet("/.well-known/jwks.json", () =>
        {
            Interlocked.Increment(ref jwks._fetchCount);
            return jwks._available ? Results.Content(jwks._body, "application/json") : Results.StatusCode(503);
        });
        await jwks._app.StartAsync();
        jwks.Url = new Uri(new Uri(jwks._app.Urls.Single()), "/.well-known/jwks.json");
        return jwks;
    }

    public void SetKeys(params TestKeys[] keys) =>
        _body = System.Text.Json.JsonSerializer.Serialize(new
        {
            keys = keys.Select(k => new { kty = k.PublicJwk.Kty, crv = k.PublicJwk.Crv, kid = k.Kid, use = k.PublicJwk.Use, alg = k.PublicJwk.Alg, x = k.PublicJwk.X, y = k.PublicJwk.Y }),
        });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
