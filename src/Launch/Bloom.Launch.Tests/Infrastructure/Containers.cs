using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Bloom.Launch.Tests.Containers))]

namespace Bloom.Launch.Tests;

/// <summary>One PostgreSQL, RabbitMQ and Redis container per test run; every test host gets its own database (plan R18) and connects as the database owner (plan R2). Redis is shared and isolated by fresh launch ids.</summary>
public sealed class Containers : IAsyncLifetime
{
    // Every test host keeps its own connection pool open, so the default limit of 100 runs out as the suite grows.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").WithCommand("-c", "max_connections=1000").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8.2.3").Build();

    public string RabbitMqUri => _rabbit.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    /// <summary>Test-side connection, for seeding and inspecting keys.</summary>
    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync(), _redis.StartAsync());
        Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (Redis is not null) await Redis.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>Runs <c>rabbitmqctl</c> inside the broker container and returns its stdout.</summary>
    public async Task<string> RabbitCtlAsync(params string[] args)
    {
        var result = await _rabbit.ExecAsync(["rabbitmqctl", .. args]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', args)} failed ({result.ExitCode}): {result.Stderr}");
        return result.Stdout;
    }

    /// <summary>Creates an empty database and returns its owner connection string (what the app and the test side both use).</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }
}
