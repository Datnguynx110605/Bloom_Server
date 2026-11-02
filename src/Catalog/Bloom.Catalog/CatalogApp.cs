using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Bloom.Catalog.Caching;
using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Bloom.Catalog.Observability;
using Bloom.Catalog.Services;
using Bloom.Shared;
using Bloom.Shared.Observability;
using Google.Protobuf.Reflection;
using Google.Rpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Bloom.Catalog;

public static class CatalogApp
{
    /// <summary>Builds the catalog API (<c>Catalog:Mode = api</c>) or the catalog worker (<c>worker</c>); both are this one app (HLD-ADR-014).</summary>
    /// <param name="configure">Runs right after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var options = new CatalogOptions();
        builder.Configuration.GetSection("Catalog").Bind(options);
        options.Validate();
        var api = options.IsApi;

        builder.AddBloomTelemetry(api ? "catalog-api" : "catalog-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(CatalogMetrics.MeterName));

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first (plan R3)
        builder.Services.AddSingleton<CatalogMetrics>();
        builder.Services.AddDbContext<CatalogDbContext>((sp, o) =>
            o.UseNpgsql(options.Database).AddInterceptors(sp.GetRequiredService<CacheInvalidationInterceptor>()));
        builder.Services.AddScoped<CacheInvalidation>();
        builder.Services.AddScoped<CacheInvalidationInterceptor>();
        builder.Services.AddScoped<Audit>();
        builder.Services.AddScoped<ProductDerived>();
        builder.Services.AddSingleton<MarkdownRenderer>();
        var bloom = builder.Services.AddBloom<CatalogDbContext>(o =>
        {
            o.ServiceName = "catalog";
            o.RabbitMqUri = options.RabbitMq;
        });
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redis = ConfigurationOptions.Parse(options.Redis);
            redis.AbortOnConnectFail = false; // start and serve while Redis is down (plan R10)
            redis.ConnectTimeout = 1000;
            redis.AsyncTimeout = 1000;
            redis.SyncTimeout = 1000; // commands queued while disconnected time out by this one, not AsyncTimeout
            return ConnectionMultiplexer.Connect(redis);
        });
        builder.Services.AddSingleton<CatalogCache>();
        builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            new BasicAWSCredentials(options.Storage.AccessKey, options.Storage.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.Storage.ServiceUrl,
                AuthenticationRegion = options.Storage.Region,
                ForcePathStyle = true,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, // MinIO (plan R18)
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            }));

        if (api)
        {
            builder.Services.AddGrpc().AddJsonTranscoding(o =>
            {
                o.TypeRegistry = TypeRegistry.FromMessages(ErrorInfo.Descriptor); // error details are Any(ErrorInfo)
                o.JsonSettings.WriteInt64sAsStrings = true; // proto3 JSON: int64 is a string (version)
            });
        }
        else
        {
            bloom.AddWorker();
        }

        var app = builder.Build();
        if (api)
        {
            app.MapGrpcService<PublicService>();
            app.MapGrpcService<AdminService>();
        }
        return app;
    }
}
