using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Gateway.Tests;

public sealed class TestGatewayOptions
{
    /// <summary>Configuration overrides, applied on top of the gateway's real appsettings.json.</summary>
    public Dictionary<string, string?> Config { get; } = [];

    /// <summary>When true (default), <see cref="TestGateway.StartAsync"/> returns once <c>/readyz</c> answers 200.</summary>
    public bool WaitForReady { get; set; } = true;

    /// <summary>False makes the JWKS endpoint answer 503 from the start.</summary>
    public bool JwksAvailable { get; set; } = true;
}

/// <summary>The gateway built through <see cref="GatewayApp.Build"/> on real Kestrel, in front of its own <see cref="EchoUpstream"/>.</summary>
public sealed class TestGateway : IAsyncDisposable
{
    private static readonly string[] Clusters =
        ["catalog", "identity", "order", "launch", "inventory", "payment", "fulfillment", "notification"];

    private static int _ipCounter;

    private readonly WebApplication _app;

    private TestGateway(WebApplication app) => _app = app;

    public required Uri Address { get; init; }

    public required HttpClient Client { get; init; }

    /// <summary>Unique per instance, from 198.18.0.0/15, so rate-limit keys never collide across tests.</summary>
    public required string ClientIp { get; init; }

    public required EchoUpstream Upstream { get; init; }

    /// <summary>Serves <see cref="DefaultKey"/> until a test swaps the keys.</summary>
    public required FakeJwks Jwks { get; init; }

    public required TestKeys DefaultKey { get; init; }

    /// <summary>The gateway's <see cref="TimeProvider"/>: token lifetimes and JWKS timing. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestGateway> StartAsync(Containers containers, Action<TestGatewayOptions>? configure = null)
    {
        var upstream = await EchoUpstream.StartAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var defaultKey = TestKeys.Create("test-key-1", clock);
        var jwks = await FakeJwks.StartAsync(defaultKey);
        var logs = new LogCapture();
        var options = new TestGatewayOptions();
        foreach (var cluster in Clusters)
            options.Config[$"ReverseProxy:Clusters:{cluster}:Destinations:d1:Address"] = $"{upstream.Address}{cluster}/";
        options.Config["Gateway:TrustedProxies:0"] = "127.0.0.1/32";
        options.Config["Gateway:AllowedOrigins:0"] = "https://bloom.test";
        options.Config["Gateway:Redis"] = containers.RedisConnectionString;
        options.Config["Gateway:RabbitMq"] = containers.RabbitMqUri;
        options.Config["Gateway:JwksUrl"] = jwks.Url.ToString();
        options.Config["urls"] = "http://127.0.0.1:0";
        configure?.Invoke(options);

        jwks.Available = options.JwksAvailable;
        var app = GatewayApp.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(options.Config);
            b.Services.AddSingleton<TimeProvider>(clock);
            b.Services.AddSingleton<ILoggerProvider>(logs);
        });
        await app.StartAsync();

        var address = new Uri(app.Urls.Single());
        var gateway = new TestGateway(app)
        {
            Upstream = upstream,
            Jwks = jwks,
            DefaultKey = defaultKey,
            Clock = clock,
            Logs = logs,
            ClientIp = NextClientIp(),
            Address = address,
            Client = new HttpClient { BaseAddress = address },
        };
        gateway.Client.DefaultRequestHeaders.Add("X-Forwarded-For", gateway.ClientIp);
        if (options.WaitForReady)
            await Poll.UntilAsync(async () => (await gateway.Client.GetAsync("/readyz", TestContext.Current.CancellationToken)).IsSuccessStatusCode, 10, "/readyz is 200");
        return gateway;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        await Upstream.DisposeAsync();
        await Jwks.DisposeAsync();
    }

    private static string NextClientIp()
    {
        var n = Interlocked.Increment(ref _ipCounter); // 198.18.0.1 … 198.19.255.255
        return new IPAddress([198, (byte)(18 + ((n >> 16) & 1)), (byte)(n >> 8), (byte)n]).ToString();
    }
}
