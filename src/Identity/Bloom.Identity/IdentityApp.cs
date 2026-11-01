using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Observability;
using Bloom.Identity.Services;
using Bloom.Identity.Sessions;
using Bloom.Shared;
using Bloom.Shared.Observability;
using Google.Protobuf.Reflection;
using Google.Rpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Bloom.Identity;

public static class IdentityApp
{
    /// <summary>Builds the identity API (<c>Identity:Mode = api</c>) or the identity worker (<c>worker</c>); both are this one app (HLD-ADR-014).</summary>
    /// <param name="configure">Runs right after the default configuration, so tests can override any key.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // Content root = the app's own folder, so appsettings.json is found wherever the process starts.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
        configure?.Invoke(builder);

        var options = new IdentityOptions();
        builder.Configuration.GetSection("Identity").Bind(options);
        options.Validate();
        var api = options.IsApi;

        builder.AddBloomTelemetry(api ? "identity-api" : "identity-worker");
        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(IdentityMetrics.MeterName));

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System); // tests register a fake first (plan I3)
        builder.Services.AddSingleton<IdentityMetrics>();
        builder.Services.AddDbContext<IdentityDbContext>(o => o.UseNpgsql(options.Database));
        builder.Services.AddBloom<IdentityDbContext>(o =>
        {
            o.ServiceName = "identity";
            o.RabbitMqUri = options.RabbitMq;
        });

        if (api)
        {
            builder.Services.AddGrpc().AddJsonTranscoding(o =>
                o.TypeRegistry = TypeRegistry.FromMessages(ErrorInfo.Descriptor)); // error details are Any(ErrorInfo)
            builder.Services.AddSingleton<SigningKeys>();
            builder.Services.AddSingleton<TokenIssuer>();
            builder.Services.AddScoped<SessionService>();
            builder.Services.AddSingleton<PasswordHasher>();
            builder.Services.AddSingleton<PasswordPolicy>();
            builder.Services.AddSingleton(new CodeHasher(options.CodePepperBytes));
            builder.Services.AddKeyedSingleton("code", new SecretBox(options.CodeEncryptionKeyBytes));
            builder.Services.AddKeyedSingleton("totp", new SecretBox(options.TotpKeyBytes));
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redis = ConfigurationOptions.Parse(options.Redis);
                redis.AbortOnConnectFail = false; // start and serve while Redis is down
                return ConnectionMultiplexer.Connect(redis);
            });
        }

        var app = builder.Build();
        if (api)
        {
            app.MapGrpcService<AuthService>();
            app.MapGet("/.well-known/jwks.json", (SigningKeys keys) => Results.Text(keys.JwksJson, "application/json"));
        }
        return app;
    }
}
