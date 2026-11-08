using Bloom.Payment.Consumers;
using Bloom.Payment.Data;
using Bloom.Payment.Domain;
using Bloom.Payment.Observability;
using Bloom.Payment.Services;
using Bloom.Payment.Stripe;
using Bloom.Payment.Worker;
using Bloom.Shared;
using Bloom.Shared.Observability;
using Google.Protobuf.Reflection;
using Google.Rpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bloom.Payment;

public static class PaymentApp
{
    /// <summary>Builds the payment API (<c>Payment:Mode = api</c>) or the payment worker (<c>worker</c>); both are this one app (HLD-ADR-014).</summary>
    /// <param name="configure">Runs right after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var options = new PaymentOptions();
        builder.Configuration.GetSection("Payment").Bind(options);
        options.Validate();
        var api = options.IsApi;

        builder.AddBloomTelemetry(api ? "payment-api" : "payment-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(PaymentMetrics.MeterName));

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first (plan R3)
        builder.Services.AddSingleton<PaymentMetrics>();
        builder.Services.AddScoped<Transitions>();
        builder.Services.AddDbContext<PaymentDbContext>(o => o.UseNpgsql(options.Database));
        var bloom = builder.Services.AddBloom<PaymentDbContext>(o =>
        {
            o.ServiceName = "payment";
            o.RabbitMqUri = options.RabbitMq;
        });

        bloom.AddConsumer<RefundPaymentConsumer>("refund-payment", RefundPaymentConsumer.Bindings); // both modes, for the DLQ catalog (plan R6)

        builder.Services.AddHttpClient<IPaymentProvider, StripeClient>(c => StripeClient.Configure(c, options.Stripe));

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
            builder.Services.AddHostedService<ProviderExecutor>();
        }

        var app = builder.Build();
        if (api)
        {
            app.MapGrpcService<InternalService>(); // internal, no HTTP route (spec §6)
            app.MapStripeWebhook(); // plain HTTP: the signature needs the raw body (04-payment §3)
            app.MapBloomDeadLetterAdmin(); // 00-reliability §7.7
        }
        return app;
    }
}
