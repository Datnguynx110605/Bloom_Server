using Bloom.Shared.Observability;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy;

namespace Bloom.Gateway;

public static class GatewayApp
{
    /// <summary>Builds the api-gateway (<c>Gateway:Mode = api</c>) or the gateway-worker (<c>worker</c>); both are this one app.</summary>
    /// <param name="configure">Runs after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false; // Kestrel adds Server after OnStarting callbacks; there is no config key for it (plan P11)
            builder.Configuration.GetSection("Kestrel:Limits").Bind(o.Limits); // body and header size limits (LLD 07 §5.2, §8)
        });

        var api = builder.Configuration["Gateway:Mode"] switch
        {
            "api" => true,
            "worker" => false,
            var mode => throw new InvalidOperationException($"Gateway:Mode must be 'api' or 'worker', not '{mode}'."),
        };

        builder.AddBloomTelemetry(api ? "api-gateway" : "gateway-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(GatewayMetrics.MeterName));

        if (api)
        {
            builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first
            builder.Services.AddSingleton<GatewayMetrics>();
            builder.Services.AddSingleton<JwksCache>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<JwksCache>());
            builder.Services.AddSingleton<TokenValidator>();
            builder.Services.AddRequestTimeouts(o => o.DefaultPolicy = new() { Timeout = TimeSpan.FromSeconds(5) }); // YARP applies each route's Timeout through it; LLD 07 §5.2 default 5 s
            builder.Services.AddReverseProxy()
                .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
                .AddTransforms(c => c.UseDefaultForwarders = false); // no X-Forwarded-* the LLD doesn't list (plan P9)
            builder.Services.Configure<ForwardedHeadersOptions>(o =>
            {
                // Only the ingress controller's addresses may supply X-Forwarded-For (GW-06, LLD 07 §6.4).
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                o.KnownIPNetworks.Clear();
                o.KnownProxies.Clear();
                o.ForwardLimit = null;
                foreach (var cidr in builder.Configuration.GetSection("Gateway:TrustedProxies").Get<string[]>() ?? [])
                    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            });
        }

        var app = builder.Build();
        if (api)
        {
            app.UseForwardedHeaders();
            app.UseMiddleware<RequestHygiene>();
            app.UseRouting();
            app.UseMiddleware<EdgeMiddleware>();
            app.UseRequestTimeouts();
            app.MapReverseProxy(ProxyPipeline.Configure);
            app.MapGet("/readyz", (JwksCache jwks, IProxyStateLookup routes) =>
                jwks.IsLoaded && routes.GetRoutes().Any() ? Results.Ok() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
        app.MapGet("/healthz", () => Results.Ok());
        return app;
    }
}
