using Bloom.Catalog.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;

namespace Bloom.Catalog.Tests;

public sealed class TestCatalogOptions
{
    public string Mode { get; set; } = "api";

    /// <summary>Reuses this database (for example an API host and a worker host on one database) instead of creating one.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Configuration overrides, applied on top of the real appsettings.json.</summary>
    public Dictionary<string, string?> Config { get; } = [];
}

/// <summary>The catalog app built through <see cref="CatalogApp.Build"/> on <c>TestServer</c> (plan R1), on its own database, bucket and Redis database (plan R27).</summary>
public sealed class TestCatalog : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestCatalog(WebApplication app) => _app = app;

    public required HttpClient Http { get; init; }

    /// <summary>The app's <see cref="TimeProvider"/>. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    public required string ConnectionString { get; init; }

    public required string Bucket { get; init; }

    /// <summary>The host's own logical Redis database.</summary>
    public required IDatabase Redis { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestCatalog> StartAsync(Containers containers, Action<TestCatalogOptions>? configure = null)
    {
        var options = new TestCatalogOptions();
        configure?.Invoke(options);
        var fresh = options.ConnectionString is null;
        var connectionString = options.ConnectionString ?? await containers.CreateDatabaseAsync();
        var bucket = await containers.CreateBucketAsync();
        var redisDatabase = containers.NextRedisDatabase();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new LogCapture();

        var config = new Dictionary<string, string?>
        {
            ["Catalog:Mode"] = options.Mode,
            ["Catalog:Database"] = connectionString,
            ["Catalog:Redis"] = containers.RedisConnectionString(redisDatabase),
            ["Catalog:RabbitMq"] = containers.RabbitMqUri,
            ["Catalog:Storage:ServiceUrl"] = containers.MinioUrl,
            ["Catalog:Storage:AccessKey"] = Containers.MinioAccessKey,
            ["Catalog:Storage:SecretKey"] = Containers.MinioSecretKey,
            ["Catalog:Storage:Bucket"] = bucket,
            ["Catalog:Storage:PublicBaseUrl"] = $"{containers.MinioUrl}/{bucket}",
        };
        foreach (var (key, value) in options.Config) config[key] = value;

        var app = CatalogApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(clock); // registered first, so the app's TryAdd keeps it
            b.Services.AddSingleton<ILoggerProvider>(logs);
        });
        if (fresh)
        {
            await using var db = NewDb(connectionString);
            await db.Database.MigrateAsync();
        }
        await app.StartAsync();
        return new TestCatalog(app)
        {
            Http = app.GetTestClient(),
            Clock = clock,
            Logs = logs,
            ConnectionString = connectionString,
            Bucket = bucket,
            Redis = containers.Redis.GetDatabase(redisDatabase),
        };
    }

    /// <summary>A test-side context on the same database.</summary>
    public CatalogDbContext NewDb() => NewDb(ConnectionString);

    private static CatalogDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(connectionString).Options);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.DisposeAsync();
    }
}
