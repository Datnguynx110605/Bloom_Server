using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(Bloom.Fulfillment.Tests.Containers))]

namespace Bloom.Fulfillment.Tests;

/// <summary>One PostgreSQL and RabbitMQ container per test run; every test host gets its own database (plan R23) and connects as the database owner (plan R2).</summary>
public sealed class Containers : IAsyncLifetime
{
    // Every test host keeps its own connection pool open, so the default limit of 100 runs out as the suite grows.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").WithCommand("-c", "max_connections=1000").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management").Build();

    public string RabbitMqUri => _rabbit.GetConnectionString();

    public async ValueTask InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
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
