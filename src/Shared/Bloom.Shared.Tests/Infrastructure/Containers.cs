using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Bloom.Shared.Tests.Containers))]

namespace Bloom.Shared.Tests;

/// <summary>One Postgres container per test run; every test gets its own database.</summary>
public sealed class Containers : IAsyncLifetime
{
    // Same exact tag as the Compose file (plan Task 11).
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

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
