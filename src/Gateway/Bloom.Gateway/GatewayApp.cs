using Bloom.Shared.Observability;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using StackExchange.Redis;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;

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

        // A missing key fails the build and names the key, never its value (GW-4).
        string Required(string key) => builder.Configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"{key} is required.");
        var redisConnection = Required("Gateway:Redis");

        builder.AddBloomTelemetry(api ? "api-gateway" : "gateway-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(GatewayMetrics.MeterName));
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(o => // the request span and the forwarded request's span both carry the secret
        {
            o.EnrichWithHttpRequest = (activity, _) => ShippoSecretRedaction.Redact(activity);
        });
        builder.Services.Configure<HttpClientTraceInstrumentationOptions>(o =>
        {
            o.EnrichWithHttpRequestMessage = (activity, _) => ShippoSecretRedaction.Redact(activity);
        });

        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnection);
            options.AbortOnConnectFail = false; // start and serve while Redis is down (P16)
            options.BacklogPolicy = BacklogPolicy.FailFast; // an outage fails requests at once instead of queueing them
            return ConnectionMultiplexer.Connect(options);
        });

        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first
        if (api)
        {
            builder.Services.AddSingleton<GatewayMetrics>();
            builder.Services.AddHostedService<RedisDegradedProbe>();
            builder.Services.AddSingleton(RateLimitPolicy.Bind(builder.Configuration));
            builder.Services.AddSingleton<EdgeStore>();
            builder.Services.AddSingleton<FallbackLimiters>();
            builder.Services.AddSingleton<JwksCache>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<JwksCache>());
            builder.Services.AddSingleton<TokenValidator>();
            builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
                .WithOrigins(builder.Configuration.GetSection("Gateway:AllowedOrigins").Get<string[]>() ?? [])
                .AllowCredentials() // the refresh cookie (LLD 07 §6.4)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
            builder.Services.AddRequestTimeouts(o => o.DefaultPolicy = new() { Timeout = TimeSpan.FromSeconds(5) }); // YARP applies each route's Timeout through it; LLD 07 §5.2 default 5 s
            builder.Services.AddReverseProxy()
                .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
                .AddTransforms(c => c.UseDefaultForwarders = false); // no X-Forwarded-* the LLD doesn't list (plan P9)
            builder.Services.Configure<ForwardedHeadersOptions>(o =>
            {
                // Only the ingress controller's addresses may supply X-Forwarded-For (GW-06, LLD 07 §6.4).
                var trusted = builder.Configuration.GetSection("Gateway:TrustedProxies").Get<string[]>() ?? [];
                // With no known proxy the middleware would trust every hop, so then it must not read the header at all.
                o.ForwardedHeaders = trusted.Length == 0 ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor;
                o.KnownIPNetworks.Clear();
                o.KnownProxies.Clear();
                o.ForwardLimit = null;
                foreach (var cidr in trusted)
                    o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            });
        }
        else
        {
            Required("Gateway:RabbitMq");
            builder.Services.AddSingleton<RevocationWriter>();
            builder.Services.AddHostedService<RevocationConsumer>();
        }

        var app = builder.Build();
        if (api)
        {
            // A route with a bad Auth or Policy fails here, naming the route, not on its first request (GW-4).
            var policies = app.Services.GetRequiredService<IReadOnlyDictionary<string, RateLimitPolicy>>();
            foreach (var route in app.Services.GetRequiredService<IProxyConfigProvider>().GetConfig().Routes)
                EdgeRoute.From(route, policies);
            app.Use(RequestHygiene.ReplaceUntrustedForwarded);
            app.UseForwardedHeaders();
            app.UseMiddleware<RequestHygiene>();
            app.UseCors(); // before route matching, so error responses carry CORS headers too (§3 step 3)
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
