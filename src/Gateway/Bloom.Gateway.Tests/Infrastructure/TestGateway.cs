using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Bloom.Gateway.Tests;

public sealed class TestGatewayOptions
{
    /// <summary>Configuration overrides, applied on top of the gateway's real appsettings.json.</summary>
    public Dictionary<string, string?> Config { get; } = [];
}

/// <summary>The gateway built through <see cref="GatewayApp.Build"/> on real Kestrel, in front of its own <see cref="EchoUpstream"/>.</summary>
public sealed class TestGateway : IAsyncDisposable
{
    private static readonly string[] Clusters =
        ["catalog", "identity", "order", "launch", "inventory", "payment", "fulfillment", "notification"];

    private static int _ipCounter;

    private readonly WebApplication _app;

    private TestGateway(WebApplication app, EchoUpstream upstream, string clientIp)
    {
        _app = app;
        Upstream = upstream;
        ClientIp = clientIp;
    }

    public Uri Address { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Unique per instance, from 198.18.0.0/15, so rate-limit keys never collide across tests.</summary>
    public string ClientIp { get; }

    public EchoUpstream Upstream { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestGateway> StartAsync(Containers containers, Action<TestGatewayOptions>? configure = null)
    {
        var upstream = await EchoUpstream.StartAsync();
        var options = new TestGatewayOptions();
        foreach (var cluster in Clusters)
            options.Config[$"ReverseProxy:Clusters:{cluster}:Destinations:d1:Address"] = $"{upstream.Address}{cluster}/";
        options.Config["Gateway:TrustedProxies:0"] = "127.0.0.1/32";
        options.Config["Gateway:AllowedOrigins:0"] = "https://bloom.test";
        options.Config["Gateway:Redis"] = containers.RedisConnectionString;
        options.Config["Gateway:RabbitMq"] = containers.RabbitMqUri;
        options.Config["urls"] = "http://127.0.0.1:0";
        configure?.Invoke(options);

        var app = GatewayApp.Build([], b => b.Configuration.AddInMemoryCollection(options.Config));
        await app.StartAsync();

        var gateway = new TestGateway(app, upstream, NextClientIp());
        gateway.Address = new Uri(app.Urls.Single());
        gateway.Client = new HttpClient { BaseAddress = gateway.Address };
        gateway.Client.DefaultRequestHeaders.Add("X-Forwarded-For", gateway.ClientIp);
        return gateway;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        await Upstream.DisposeAsync();
    }

    private static string NextClientIp()
    {
        var n = Interlocked.Increment(ref _ipCounter); // 198.18.0.1 … 198.19.255.255
        return new IPAddress([198, (byte)(18 + ((n >> 16) & 1)), (byte)(n >> 8), (byte)n]).ToString();
    }
}
