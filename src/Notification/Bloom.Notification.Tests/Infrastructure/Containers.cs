using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Bloom.Notification.Tests.Containers))]

namespace Bloom.Notification.Tests;

/// <summary>One PostgreSQL, RabbitMQ, Redis and Mailpit per test run (plan N11); every test host gets its own database and connects as the database owner (plan N2).</summary>
public sealed class Containers : IAsyncLifetime
{
    // Every test host keeps its own connection pool open, so the default limit of 100 runs out as the suite grows.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").WithCommand("-c", "max_connections=1000").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4").Build();
    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:v1.33.1")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/api/v1/info")))
        .Build();

    public string RabbitMqUri => _rabbit.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public string SmtpHost => _mailpit.Hostname;

    public int SmtpPort => _mailpit.GetMappedPublicPort(1025);

    /// <summary>The Mailpit REST API.</summary>
    public Uri MailpitApi => new($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}/");

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync(), _redis.StartAsync(), _mailpit.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _redis.DisposeAsync();
        await _mailpit.DisposeAsync();
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
