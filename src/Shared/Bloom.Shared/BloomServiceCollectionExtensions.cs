using System.Text.RegularExpressions;
using Bloom.Shared.Concurrency;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Idempotency;
using Bloom.Shared.Messaging;
using Bloom.Shared.Observability;
using Bloom.Shared.Retention;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bloom.Shared;

public sealed class BloomBuilder(IServiceCollection services, string serviceName)
{
    public IServiceCollection Services { get; } = services;

    internal ConsumerCatalog Catalog { get; } = new();

    /// <summary>Registers a consumer: queue <c>{service}.{handler}</c>, handler resolved from a scope per delivery.</summary>
    public BloomBuilder AddConsumer<THandler>(string handler, params ConsumerBinding[] bindings)
        where THandler : class, IMessageHandler
    {
        Services.AddScoped<THandler>();
        Catalog.Add(new ConsumerDefinition(handler, MessageNames.ConsumerQueue(serviceName, handler), typeof(THandler), bindings));
        return this;
    }

    /// <summary>Declares <c>{target}.commands</c> exchanges this service sends commands to.</summary>
    public BloomBuilder SendsCommandsTo(params string[] services)
    {
        Catalog.AddCommandTargets(services);
        return this;
    }

    /// <summary>Background work, in registration order: topology first. Later tasks add their hosted services here.</summary>
    public BloomBuilder AddWorker()
    {
        Services.AddHostedService<TopologyInitializer>();
        Services.AddHostedService<OutboxRelay>();
        Services.AddHostedService<ConsumerHost>();
        Services.AddHostedService<RetentionPurger>();
        Services.AddHostedService<GaugePoller>();
        return this;
    }
}

public static partial class BloomServiceCollectionExtensions
{
    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex ServiceNamePattern();

    public static BloomBuilder AddBloom<TDbContext>(this IServiceCollection services, Action<BloomOptions> configure)
        where TDbContext : DbContext
    {
        var options = new BloomOptions();
        configure(options);
        if (!ServiceNamePattern().IsMatch(options.ServiceName))
            throw new ArgumentException(
                $"ServiceName must match ^[a-z][a-z0-9-]*$ but was '{options.ServiceName}'.", nameof(configure));

        services.AddSingleton(options);
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<TDbContext>());
        services.AddHttpContextAccessor();
        services.AddMetrics();
        services.AddSingleton<BloomMetrics>();
        services.AddScoped<ConcurrentUpdates>();
        services.AddScoped<OutboxWriter>();
        services.AddScoped<IdempotencyGuard>();
        var builder = new BloomBuilder(services, options.ServiceName);
        services.AddSingleton(builder.Catalog);
        services.AddSingleton<RabbitMqConnection>();
        services.AddSingleton<DeadLetterPass>();
        return builder;
    }

    /// <summary>Hosts the <c>DeadLetterAdmin</c> gRPC service (00-reliability §7.7). The host must call <c>AddGrpc()</c> (and <c>AddJsonTranscoding()</c> for the HTTP routes).
    /// It must also register the same <c>AddConsumer</c> calls as the worker, or the DLQ catalog is empty and every queue is <c>DLQ_NOT_FOUND</c>.</summary>
    public static IEndpointRouteBuilder MapBloomDeadLetterAdmin(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<DeadLetterAdminService>();
        return endpoints;
    }

    /// <summary><c>GET /healthz</c> (process alive) in both modes. In API mode also <c>GET /readyz</c>: 200 once the host has started, 503 before that and from the moment shutdown begins.
    /// No dependency is checked (HLD-ADR-020, HLD §9.5).</summary>
    public static IEndpointRouteBuilder MapBloomHealth(this IEndpointRouteBuilder endpoints, bool api)
    {
        endpoints.MapGet("/healthz", () => Results.Ok());
        if (api)
            endpoints.MapGet("/readyz", (IHostApplicationLifetime lifetime) =>
                lifetime.ApplicationStarted.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested
                    ? Results.Ok()
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        return endpoints;
    }
}
