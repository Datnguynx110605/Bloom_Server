using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bloom.Shared.Tests;

public static class TestApp
{
    /// <summary>Host on a fresh database with the schema created. Not started.</summary>
    public static async Task<IHost> BuildAsync(Containers containers, Action<BloomBuilder>? configure = null)
    {
        var cs = await containers.CreateDatabaseAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<TestDbContext>(o => o.UseNpgsql(cs));
        var bloom = builder.Services.AddBloom<TestDbContext>(o =>
        {
            o.ServiceName = "t" + Guid.NewGuid().ToString("N")[..8];
            o.RabbitMqUri = containers.RabbitMqUri;
        });
        configure?.Invoke(bloom);
        var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        return host;
    }
}
