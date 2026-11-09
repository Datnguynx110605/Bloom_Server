using System.Data.Common;
using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StackExchange.Redis;

namespace Bloom.Launch.Tests;

public sealed class TestLaunchOptions
{
    public string Mode { get; set; } = "api";

    /// <summary>Reuses this database (for example an API host and a worker host on one database) instead of creating one.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Uses this catalog double instead of a new one, so a test can script it before the host starts.</summary>
    public FakeCatalog? Catalog { get; set; }

    /// <summary>Config keys (for example <c>Launch:Redis</c>) that replace the defaults of <see cref="TestLaunch.DefaultConfig"/>.</summary>
    public Dictionary<string, string?> Config { get; } = [];
}

/// <summary>The launch app built through <see cref="LaunchApp.Build"/> on <c>TestServer</c> (plan R1), on its own database (plan R18).</summary>
public sealed class TestLaunch : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly GrpcChannel _channel;
    private readonly bool _ownsCatalog;

    private TestLaunch(WebApplication app, GrpcChannel channel, bool ownsCatalog)
    {
        _app = app;
        _channel = channel;
        _ownsCatalog = ownsCatalog;
        Internal = new(channel);
        Customer = new(channel);
        Admin = new(channel);
    }

    /// <summary>The internal gRPC client, called in-process (plan R1).</summary>
    public LaunchInternal.LaunchInternalClient Internal { get; }

    public LaunchCustomer.LaunchCustomerClient Customer { get; }

    public LaunchAdmin.LaunchAdminClient Admin { get; }

    /// <summary>Counts the database commands the app runs.</summary>
    public required CommandCounter Commands { get; init; }

    public required HttpClient Http { get; init; }

    /// <summary>The app's <see cref="TimeProvider"/>. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    /// <summary>The catalog double the app's <c>Launch:Catalog</c> points at.</summary>
    public required FakeCatalog Catalog { get; init; }

    /// <summary>The connection string of the database (the app connects as its owner, plan R2).</summary>
    public required string ConnectionString { get; init; }

    /// <summary>The run's Redis, from the test side.</summary>
    public required IDatabase Redis { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestLaunch> StartAsync(Containers containers, Action<TestLaunchOptions>? configure = null)
    {
        var options = new TestLaunchOptions();
        configure?.Invoke(options);
        var fresh = options.ConnectionString is null;
        var connectionString = options.ConnectionString ?? await containers.CreateDatabaseAsync();
        // PostgreSQL keeps microseconds; Linux clocks have 100 ns ticks
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));
        var logs = new LogCapture();
        var commands = new CommandCounter();
        var catalog = options.Catalog ?? await FakeCatalog.StartAsync();

        var config = DefaultConfig(connectionString, containers.RabbitMqUri, containers.RedisConnectionString, catalog.Address.ToString());
        config["Launch:Mode"] = options.Mode;
        foreach (var (key, value) in options.Config) config[key] = value;

        var app = LaunchApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(clock); // registered first, so the app's TryAdd keeps it
            b.Services.AddSingleton<ILoggerProvider>(logs);
            b.Services.ConfigureDbContext<LaunchDbContext>(o => o.AddInterceptors(commands));
        });
        if (fresh)
        {
            await using var db = NewDb(connectionString);
            await db.Database.MigrateAsync(); // the app never migrates (plan R2)
        }
        await app.StartAsync();
        if (!options.Config.ContainsKey("Launch:Redis"))
        {
            // The app connects on first use and fails commands at once while disconnected, so a connect that the Docker port forwarder refuses once would fail the first calls of a test.
            var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
            await Poll.UntilAsync(() => Task.FromResult(redis.IsConnected), 10, "the app's Redis connection");
        }
        var server = app.GetTestServer();
        return new TestLaunch(app, GrpcChannel.ForAddress(server.BaseAddress, new() { HttpHandler = server.CreateHandler() }), options.Catalog is null)
        {
            Http = app.GetTestClient(),
            Clock = clock,
            Logs = logs,
            Commands = commands,
            Catalog = catalog,
            ConnectionString = connectionString,
            Redis = containers.Redis.GetDatabase(),
        };
    }

    /// <summary>Every key of plan R4 except <c>Mode</c>, with valid values: two pass keys, <c>k1</c> (current) and <c>k0</c>, of 32 random bytes each.</summary>
    internal static Dictionary<string, string?> DefaultConfig(string database, string rabbitMq, string redis, string catalog) => new()
    {
        ["Launch:Database"] = database,
        ["Launch:RabbitMq"] = rabbitMq,
        ["Launch:Redis"] = redis,
        ["Launch:Catalog"] = catalog,
        ["Launch:PassKeys:0:Kid"] = "k1",
        ["Launch:PassKeys:0:Key"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        ["Launch:PassKeys:1:Kid"] = "k0",
        ["Launch:PassKeys:1:Key"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        ["Launch:CurrentPassKid"] = "k1",
        ["Launch:AdmissionCeiling"] = "200",
    };

    /// <summary>A test-side context on the same database.</summary>
    public LaunchDbContext NewDb() => NewDb(ConnectionString);

    private static LaunchDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<LaunchDbContext>().UseNpgsql(connectionString).Options);

    public async Task SqlAsync(FormattableString sql)
    {
        await using var db = NewDb();
        await db.Database.ExecuteSqlAsync(sql, TestContext.Current.CancellationToken);
    }

    public int ErrorLogs => Logs.Entries.Count(e => e.StartsWith("[Error]", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        _channel.Dispose();
        await _app.DisposeAsync();
        if (_ownsCatalog) await Catalog.DisposeAsync();
    }
}

/// <summary>Counts every command the app's <c>DbContext</c> runs (added to the app's context options by the test host).</summary>
public sealed class CommandCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => _count;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, ct);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _count);
        return base.NonQueryExecutingAsync(command, eventData, result, ct);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _count);
        return base.ScalarExecutingAsync(command, eventData, result, ct);
    }
}
