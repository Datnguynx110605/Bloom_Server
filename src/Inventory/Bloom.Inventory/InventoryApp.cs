using Bloom.Catalog.Contracts;
using Bloom.Inventory.Consumers;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Inventory.Observability;
using Bloom.Inventory.Services;
using Bloom.Inventory.Worker;
using Bloom.Shared;
using Bloom.Shared.Messaging;
using Bloom.Shared.Observability;
using Bloom.Shared.Persistence;
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
        builder.Services.AddSingleton<AfterCommitInterceptor>();
        builder.Services.AddDbContext<InventoryDbContext>((sp, o) => o.UseNpgsql(options.Database).AddInterceptors(sp.GetRequiredService<AfterCommitInterceptor>()));
        var bloom = builder.Services.AddBloom<InventoryDbContext>(o =>
        {
            o.ServiceName = "inventory";
            o.RabbitMqUri = options.RabbitMq;
        });
        // Both modes: the DLQ admin of the API needs the consumer catalog as much as the worker does (plan R6).
        bloom.AddConsumer<OrderPaidConsumer>("order-paid", OrderPaidConsumer.Bindings);
        bloom.AddConsumer<ReleaseReservationConsumer>("release-reservation", ReleaseReservationConsumer.Bindings);
        bloom.AddConsumer<ShipmentShippedConsumer>("shipment-shipped", ShipmentShippedConsumer.Bindings);
        bloom.AddConsumer<CustomerReturnConsumer>("receive-customer-return", CustomerReturnConsumer.Bindings);
        bloom.AddConsumer<ReturnedToSenderConsumer>("receive-returned-to-sender", ReturnedToSenderConsumer.Bindings);
        bloom.AddConsumer<LostInTransitConsumer>("mark-lost-in-transit", LostInTransitConsumer.Bindings);
        builder.Services.AddScoped<Ledger>();
        builder.Services.AddScoped<StockLocks>();
        builder.Services.AddScoped<Allocator>();
        builder.Services.AddScoped<Audit>();
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
            builder.Services.AddHostedService<StockMonitor>();
            builder.Services.AddHostedService<TombstonePurger>();
        }

        var app = builder.Build();
        if (api)
        {
            app.MapGrpcService<WarehouseService>();
            app.MapGrpcService<StockService>(); // internal, no HTTP route (spec §6)
            app.MapGrpcService<PackingService>(); // internal, like StockService
            app.MapBloomDeadLetterAdmin(); // 00-reliability §7.7
        }
        app.MapBloomHealth(api); // HLD-ADR-020
        return app;
    }
}
