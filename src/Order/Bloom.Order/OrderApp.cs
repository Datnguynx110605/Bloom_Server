using Bloom.Catalog.Contracts;
using Bloom.Fulfillment.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Consumers;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Observability;
using Bloom.Order.Services;
using Bloom.Order.Worker;
using Bloom.Payment.Contracts;
using Bloom.Shared;
using Bloom.Shared.Observability;
using Google.Protobuf.Reflection;
using Google.Rpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bloom.Order;

public static class OrderApp
{
    /// <summary>Builds the order API (<c>Order:Mode = api</c>) or the order worker (<c>worker</c>); both are this one app (HLD-ADR-014).</summary>
    /// <param name="configure">Runs right after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var options = new OrderOptions();
        builder.Configuration.GetSection("Order").Bind(options);
        options.Validate();
        var api = options.IsApi;

        builder.AddBloomTelemetry(api ? "order-api" : "order-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(OrderMetrics.MeterName));

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first (plan R3)
        builder.Services.AddSingleton<OrderMetrics>();
        builder.Services.AddDbContext<OrderDbContext>(o => o.UseNpgsql(options.Database));
        var bloom = builder.Services.AddBloom<OrderDbContext>(o =>
        {
            o.ServiceName = "order";
            o.RabbitMqUri = options.RabbitMq;
        });
        bloom.SendsCommandsTo("inventory", "launch", "payment");
        builder.Services.AddScoped<Audit>();
        builder.Services.AddScoped<Upstreams>();
        builder.Services.AddScoped<Transitions>();
        builder.Services.AddScoped<Sagas>();
        builder.Services.AddScoped<Coupons>();
        builder.Services.AddScoped<CheckoutFailures>();
        builder.Services.AddScoped<Cancellations>();
        bloom.AddConsumer<PaymentSucceededConsumer>("payment-succeeded", PaymentSucceededConsumer.Bindings); // both modes, for the DLQ catalog (plan R6)
        bloom.AddConsumer<PaymentFailedConsumer>("payment-failed", PaymentFailedConsumer.Bindings);
        bloom.AddConsumer<ReservationReleasedConsumer>("inventory-reservation-released", ReservationReleasedConsumer.Bindings);
        bloom.AddConsumer<StockAllocatedConsumer>("inventory-stock-allocated", StockAllocatedConsumer.Bindings);
        bloom.AddConsumer<LaunchStageChangedConsumer>("launch-stage-changed", LaunchStageChangedConsumer.Bindings);
        bloom.AddConsumer<RefundCompletedConsumer>("payment-refund-completed", RefundCompletedConsumer.Bindings);
        bloom.AddConsumer<RefundFailedConsumer>("payment-refund-failed", RefundFailedConsumer.Bindings);
        bloom.AddConsumer<HoldReleasedConsumer>("launch-hold-released", HoldReleasedConsumer.Bindings);
        bloom.AddConsumer<PackingStartedConsumer>("fulfillment-packing-started", PackingStartedConsumer.Bindings);
        bloom.AddConsumer<ShipmentShippedConsumer>("fulfillment-shipment-shipped", ShipmentShippedConsumer.Bindings);
        bloom.AddConsumer<ShipmentDeliveredConsumer>("fulfillment-shipment-delivered", ShipmentDeliveredConsumer.Bindings);
        bloom.AddConsumer<DeliveryFailedConsumer>("fulfillment-delivery-failed", DeliveryFailedConsumer.Bindings);
        bloom.AddConsumer<ReturnRequestedConsumer>("fulfillment-return-requested", ReturnRequestedConsumer.Bindings);
        bloom.AddConsumer<ReturnClosedConsumer>("fulfillment-return-closed", ReturnClosedConsumer.Bindings);
        bloom.AddConsumer<ReturnDecidedConsumer>("fulfillment-return-decided", ReturnDecidedConsumer.Bindings);
        builder.Services.AddGrpcClient<CatalogInternal.CatalogInternalClient>(o => o.Address = new Uri(options.Catalog));
        builder.Services.AddGrpcClient<InventoryStock.InventoryStockClient>(o => o.Address = new Uri(options.Inventory));
        builder.Services.AddGrpcClient<LaunchInternal.LaunchInternalClient>(o => o.Address = new Uri(options.Launch));
        builder.Services.AddGrpcClient<PaymentInternal.PaymentInternalClient>(o => o.Address = new Uri(options.Payment));
        builder.Services.AddGrpcClient<FulfillmentInternal.FulfillmentInternalClient>(o => o.Address = new Uri(options.Fulfillment));

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
            builder.Services.AddHostedService<Sweeps>();
        }

        var app = builder.Build();
        if (api)
        {
            app.MapGrpcService<InternalService>(); // internal, no HTTP route (spec §6)
            app.MapGrpcService<CartService>();
            app.MapGrpcService<CheckoutService>();
            app.MapGrpcService<CustomerService>();
            app.MapGrpcService<StaffService>();
        }
        return app;
    }
}
