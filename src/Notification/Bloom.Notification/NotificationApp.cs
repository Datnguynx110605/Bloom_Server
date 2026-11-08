using Bloom.Notification.Data;
using Bloom.Notification.Email;
using Bloom.Notification.Observability;
using Bloom.Notification.Services;
using Bloom.Notification.Sockets;
using Bloom.Notification.Templates;
using Bloom.Shared;
using Bloom.Shared.Observability;
using Google.Protobuf.Reflection;
using Google.Rpc;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Bloom.Notification;

public static class NotificationApp
{
    /// <summary>Builds the notification API (<c>Notification:Mode = api</c>) or the notification worker (<c>worker</c>); both are this one app (HLD-ADR-014).</summary>
    /// <param name="configure">Runs right after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var options = new NotificationOptions();
        builder.Configuration.GetSection("Notification").Bind(options);
        options.Validate();
        var api = options.IsApi;

        builder.AddBloomTelemetry(api ? "notification-api" : "notification-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(NotificationMetrics.MeterName));

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first (plan N3)
        builder.Services.AddSingleton<NotificationMetrics>();
        builder.Services.AddSingleton(new TemplateRenderer(options)); // parses every template now: a bad template fails the build of the app (plan N15)
        builder.Services.AddSingleton<AfterCommitInterceptor>();
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options.Redis)); // connects on first use, so building the app needs no Redis
        builder.Services.AddDbContext<NotificationDbContext>((sp, o) => o.UseNpgsql(options.Database).AddInterceptors(sp.GetRequiredService<AfterCommitInterceptor>()));
        if (options.Email.Provider == "resend")
        {
            builder.Services.AddHttpClient<ResendEmailProvider>(c => ResendEmailProvider.Configure(c, options.Email.Resend));
            builder.Services.AddTransient<IEmailProvider>(sp => sp.GetRequiredService<ResendEmailProvider>());
        }
        else
        {
            builder.Services.AddTransient<IEmailProvider, SmtpEmailProvider>();
        }
        builder.Services.AddSignalR(o => { o.KeepAliveInterval = TimeSpan.FromSeconds(15); o.ClientTimeoutInterval = TimeSpan.FromSeconds(30); }) // both modes: the worker pushes through IHubContext
            .AddStackExchangeRedis(options.Redis, o => o.Configuration.ChannelPrefix = RedisChannel.Literal("bloom-notification"));
        var bloom = builder.Services.AddBloom<NotificationDbContext>(o =>
        {
            o.ServiceName = "notification";
            o.RabbitMqUri = options.RabbitMq;
        });

        if (api)
        {
            builder.Services.AddSingleton<Tickets>();
            builder.Services.AddSingleton<ConnectionRegistry>();
            builder.Services.AddHostedService<DisconnectListener>();
            builder.Services.AddGrpc().AddJsonTranscoding(o =>
            {
                o.TypeRegistry = TypeRegistry.FromMessages(ErrorInfo.Descriptor, BadRequest.Descriptor); // error details are Any(ErrorInfo) and Any(BadRequest)
                o.JsonSettings.WriteInt64sAsStrings = true; // proto3 JSON: int64 is a string
            });
        }
        else
        {
            bloom.AddWorker(); // worker only (plan N6)
        }

        var app = builder.Build();
        if (api)
        {
            app.UseWebSockets(); // the gate reads IsWebSocketRequest before MapHub's own WebSockets middleware runs; Kestrel has no feature of its own
            app.UseMiddleware<HubGate>();
            app.MapHub<NotificationHub>(NotificationHub.Path, o => o.Transports = HttpTransportType.WebSockets);
            app.MapBloomDeadLetterAdmin(); // 00-reliability §7.7
            app.MapGrpcService<CustomerService>(); // transcoded to /api/notifications (spec §6)
        }
        return app;
    }
}
