using Bloom.Shared.Observability;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

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

        var api = builder.Configuration["Gateway:Mode"] switch
        {
            "api" => true,
            "worker" => false,
            var mode => throw new InvalidOperationException($"Gateway:Mode must be 'api' or 'worker', not '{mode}'."),
        };

        builder.AddBloomTelemetry(api ? "api-gateway" : "gateway-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Bloom.Gateway"));

        if (api)
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

        var app = builder.Build();
        if (api)
            app.UseForwardedHeaders();
        app.UseRouting();
        app.Use((context, next) => context.GetEndpoint() is null
            ? GatewayErrors.WriteAsync(context, GatewayErrors.RouteNotFound)
            : next(context));
        app.MapGet("/healthz", () => Results.Ok());
        return app;
    }
}
