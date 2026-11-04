using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(Bloom.Inventory.Tests.Containers))]

namespace Bloom.Inventory.Tests;

/// <summary>One PostgreSQL and RabbitMQ container per test run; every test host gets its own database (plan R22) and runs as the app role (plan R8).</summary>
public sealed class Containers : IAsyncLifetime
{
    private const string AppRole = "inventory_app";
    private const string AppPassword = "inventory-app-test";

    // Every test host keeps its own connection pool open, so the default limit of 100 runs out as the suite grows.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").WithCommand("-c", "max_connections=1000").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management").Build();

    public string RabbitMqUri => _rabbit.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE ROLE {AppRole} LOGIN PASSWORD '{AppPassword}'", conn); // a deployment step in production
        await cmd.ExecuteNonQueryAsync();
    }

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

    /// <summary>Creates an empty database and returns its owner connection string (for migrations and test-side access).</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>The same database as <paramref name="ownerConnectionString"/>, as the app role.</summary>
    public static string AsAppRole(string ownerConnectionString) =>
        new NpgsqlConnectionStringBuilder(ownerConnectionString) { Username = AppRole, Password = AppPassword }.ConnectionString;
}
