using StackExchange.Redis;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Bloom.Gateway.Tests.Containers))]

namespace Bloom.Gateway.Tests;

/// <summary>One Redis and one RabbitMQ container per test run (same tags as the Compose file).</summary>
public sealed class Containers : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:8.2.3").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management").Build();

    public string RedisConnectionString => _redis.GetConnectionString();

    /// <summary>Test-side connection, for seeding and inspecting keys.</summary>
    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public string RabbitMqUri => _rabbit.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_redis.StartAsync(), _rabbit.StartAsync());
        Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await Redis.DisposeAsync();
        await _redis.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    /// <summary>Runs <c>rabbitmqctl</c> inside the broker container and returns its stdout.</summary>
    public async Task<string> RabbitCtlAsync(params string[] args)
    {
        var result = await _rabbit.ExecAsync(["rabbitmqctl", .. args]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', args)} failed ({result.ExitCode}): {result.Stderr}");
        return result.Stdout;
    }
}
