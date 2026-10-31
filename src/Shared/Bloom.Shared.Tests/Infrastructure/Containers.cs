using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(Bloom.Shared.Tests.Containers))]

namespace Bloom.Shared.Tests;

/// <summary>One Postgres container per test run; every test gets its own database.</summary>
public sealed class Containers : IAsyncLifetime
{
    // Same exact tag as the Compose file (plan Task 11).
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").Build();

    // Same exact tag as the Compose file (plan Task 11).
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
