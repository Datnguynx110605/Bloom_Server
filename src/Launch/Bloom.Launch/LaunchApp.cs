using Bloom.Catalog.Contracts;
using Bloom.Launch.Data;
using Bloom.Launch.Observability;
using Bloom.Launch.Passes;
using Bloom.Launch.Services;
using Bloom.Launch.WaitingRoom;
using Bloom.Shared;
using Bloom.Shared.Observability;
using Google.Protobuf.Reflection;
using Google.Rpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Bloom.Launch;

public static class LaunchApp
{
    /// <summary>Builds the launch API (<c>Launch:Mode = api</c>) or the launch worker (<c>worker</c>); both are this one app (HLD-ADR-014).</summary>
    /// <param name="configure">Runs right after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var options = new LaunchOptions();
        builder.Configuration.GetSection("Launch").Bind(options);
        options.Validate();
        var api = options.IsApi;

        builder.AddBloomTelemetry(api ? "launch-api" : "launch-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(LaunchMetrics.MeterName));

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first (plan R3)
        builder.Services.AddSingleton<LaunchMetrics>();
        builder.Services.AddSingleton<PassTokens>();
        builder.Services.AddSingleton<StateCache>();
        builder.Services.AddDbContext<LaunchDbContext>(o => o.UseNpgsql(options.Database));
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redis = ConfigurationOptions.Parse(options.Redis);
            redis.AbortOnConnectFail = false; // start and serve while Redis is down (plan R3)
            // Fail commands at once while disconnected instead of queueing them: Redis down is UNAVAILABLE, not a 5 s wait (owner item 2).
            redis.BacklogPolicy = BacklogPolicy.FailFast;
            return ConnectionMultiplexer.Connect(redis);
        });
        builder.Services.AddGrpcClient<CatalogInternal.CatalogInternalClient>(o => o.Address = new Uri(options.Catalog));
        var bloom = builder.Services.AddBloom<LaunchDbContext>(o =>
        {
            o.ServiceName = "launch";
            o.RabbitMqUri = options.RabbitMq;
        });

        if (api)
        {
            builder.Services.AddGrpc().AddJsonTranscoding(o =>
            {
                o.TypeRegistry = TypeRegistry.FromMessages(ErrorInfo.Descriptor, BadRequest.Descriptor); // error details are Any(ErrorInfo) and Any(BadRequest)
                o.JsonSettings.WriteInt64sAsStrings = true; // proto3 JSON: int64 is a string
            });
        }
        else
        {
            bloom.AddWorker(); // worker only (plan R5)
        }

        var app = builder.Build();
        if (api)
        {
            app.MapGrpcService<InternalService>(); // internal, no HTTP route (spec §6)
            app.MapGrpcService<CustomerService>(); // transcoded to /api/launches (spec §6)
        }
        return app;
    }
}
