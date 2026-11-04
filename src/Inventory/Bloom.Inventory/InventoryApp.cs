using Bloom.Catalog.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Observability;
using Bloom.Shared;
using Bloom.Shared.Messaging;
using Bloom.Shared.Observability;
using Google.Protobuf.Reflection;
using Google.Rpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bloom.Inventory;

public static class InventoryApp
{
    /// <summary>Builds the inventory API (<c>Inventory:Mode = api</c>) or the inventory worker (<c>worker</c>); both are this one app (HLD-ADR-014).</summary>
    /// <param name="configure">Runs right after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var options = new InventoryOptions();
        builder.Configuration.GetSection("Inventory").Bind(options);
        options.Validate();
        var api = options.IsApi;

        builder.AddBloomTelemetry(api ? "inventory-api" : "inventory-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(InventoryMetrics.MeterName));

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first (plan R3)
        builder.Services.AddSingleton<InventoryMetrics>();
        builder.Services.AddDbContext<InventoryDbContext>(o => o.UseNpgsql(options.Database));
        var bloom = builder.Services.AddBloom<InventoryDbContext>(o =>
        {
            o.ServiceName = "inventory";
            o.RabbitMqUri = options.RabbitMq;
        });
        builder.Services.AddGrpcClient<CatalogInternal.CatalogInternalClient>(o => o.Address = new Uri(options.Catalog));

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
            bloom.AddWorker(); // worker only (plan R6)
        }

        var app = builder.Build();
        if (api) app.MapBloomDeadLetterAdmin(); // 00-reliability §7.7
        return app;
    }
}
