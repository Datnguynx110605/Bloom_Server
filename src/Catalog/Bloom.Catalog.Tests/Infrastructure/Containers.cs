using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Bloom.Catalog.Tests.Containers))]

namespace Bloom.Catalog.Tests;

/// <summary>One PostgreSQL, Redis, RabbitMQ and MinIO container per test run; every test host gets its own database, bucket and Redis database (plan R27).</summary>
public sealed class Containers : IAsyncLifetime
{
    public const string MinioAccessKey = "bloom-test";
    public const string MinioSecretKey = "bloom-test-secret";

    private int _redisDatabase;

    // Every test host keeps its own connection pool open, so the default limit of 100 runs out as the suite grows.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").WithCommand("-c", "max_connections=1000").Build();
    // One logical database per test host (host N uses database N).
    private readonly RedisContainer _redis = new RedisBuilder("redis:8.2.3").WithCommand("--databases", "4096").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management").Build();
    // minio/minio is no longer published on Docker Hub or Quay; alpine/minio mirrors the same release. It runs as user "minio" and
    // cannot write its root-owned /data, so the test container runs as root.
    private readonly MinioContainer _minio = new MinioBuilder("alpine/minio:RELEASE.2025-10-15T17-29-55Z")
        .WithUsername(MinioAccessKey).WithPassword(MinioSecretKey).WithCreateParameterModifier(p => p.User = "0").Build();

    /// <summary>Test-side connection (database 0), for seeding and inspecting keys.</summary>
    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public string RabbitMqUri => _rabbit.GetConnectionString();

    public string MinioUrl => $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";

    public IAmazonS3 S3 { get; private set; } = null!;

    public string RedisConnectionString(int database) => $"{_redis.GetConnectionString()},defaultDatabase={database}";

    /// <summary>The next free logical Redis database, starting at 1.</summary>
    public int NextRedisDatabase() => Interlocked.Increment(ref _redisDatabase);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbit.StartAsync(), _minio.StartAsync());
        Redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString(0));
        S3 = new AmazonS3Client(new BasicAWSCredentials(MinioAccessKey, MinioSecretKey), new AmazonS3Config
        {
            ServiceURL = MinioUrl,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    public async ValueTask DisposeAsync()
    {
        S3?.Dispose();
        if (Redis is not null) await Redis.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _minio.DisposeAsync();
    }

    /// <summary>Runs <c>rabbitmqctl</c> inside the broker container and returns its stdout.</summary>
    public async Task<string> RabbitCtlAsync(params string[] args)
    {
        var result = await _rabbit.ExecAsync(["rabbitmqctl", .. args]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"rabbitmqctl {string.Join(' ', args)} failed ({result.ExitCode}): {result.Stderr}");
        return result.Stdout;
    }

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    /// <summary>Creates an empty bucket and returns its name.</summary>
    public async Task<string> CreateBucketAsync()
    {
        var name = $"c{Guid.NewGuid():N}";
        await S3.PutBucketAsync(new PutBucketRequest { BucketName = name });
        return name;
    }
}
