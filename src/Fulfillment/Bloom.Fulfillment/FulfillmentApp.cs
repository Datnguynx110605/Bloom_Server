using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Observability;
using Bloom.Fulfillment.Services;
using Bloom.Inventory.Contracts;
using Bloom.Shared;
using Bloom.Shared.Observability;
using Google.Protobuf.Reflection;
using Google.Rpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bloom.Fulfillment;

public static class FulfillmentApp
{
    /// <summary>Builds the fulfillment API (<c>Fulfillment:Mode = api</c>) or the fulfillment worker (<c>worker</c>); both are this one app (HLD-ADR-014).</summary>
    /// <param name="configure">Runs right after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var options = new FulfillmentOptions();
        builder.Configuration.GetSection("Fulfillment").Bind(options);
        options.Validate();
        var api = options.IsApi;

        builder.AddBloomTelemetry(api ? "fulfillment-api" : "fulfillment-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(FulfillmentMetrics.MeterName));

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first (plan R3)
        builder.Services.AddSingleton<FulfillmentMetrics>();
        builder.Services.AddDbContext<FulfillmentDbContext>(o => o.UseNpgsql(options.Database));
        var bloom = builder.Services.AddBloom<FulfillmentDbContext>(o =>
        {
            o.ServiceName = "fulfillment";
            o.RabbitMqUri = options.RabbitMq;
        });
        bloom.SendsCommandsTo("inventory", "payment");
        builder.Services.AddGrpcClient<InventoryPacking.InventoryPackingClient>(o => o.Address = new Uri(options.Inventory));
        builder.Services.AddGrpcClient<InventoryStock.InventoryStockClient>(o => o.Address = new Uri(options.Inventory));

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
        if (api)
        {
            app.MapGrpcService<InternalService>(); // internal, no HTTP route (spec §6)
            app.MapBloomDeadLetterAdmin(); // 00-reliability §7.7
        }
        return app;
    }
}
