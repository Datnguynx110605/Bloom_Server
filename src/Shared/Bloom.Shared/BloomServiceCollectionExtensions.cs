using System.Text.RegularExpressions;
using Bloom.Shared.Concurrency;
using Bloom.Shared.Messaging;
using Bloom.Shared.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        var builder = new BloomBuilder(services, options.ServiceName);
        services.AddSingleton(builder.Catalog);
        services.AddSingleton<RabbitMqConnection>();
        return builder;
    }
}
