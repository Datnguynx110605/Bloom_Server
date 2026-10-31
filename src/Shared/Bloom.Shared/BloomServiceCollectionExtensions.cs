using System.Text.RegularExpressions;
using Bloom.Shared.Concurrency;
using Bloom.Shared.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Shared;

public sealed class BloomBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;
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
        return new BloomBuilder(services);
    }
}
