using Microsoft.AspNetCore.Builder;
using Bloom.Identity.Data;
using Bloom.Shared.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Identity.Tests;

public sealed class TestIdentityOptions
{
    public string Mode { get; set; } = "api";

    /// <summary>Reuses this database (for example an API host and a worker host on one database) instead of creating one.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Configuration overrides, applied on top of the real appsettings.json and the test secrets.</summary>
    public Dictionary<string, string?> Config { get; } = [];
}

/// <summary>The identity app built through <see cref="IdentityApp.Build"/> on <c>TestServer</c> (plan I1), on its own database.</summary>
public sealed class TestIdentity : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestIdentity(WebApplication app) => _app = app;

    public required HttpClient Http { get; init; }

    /// <summary>The app's <see cref="TimeProvider"/>. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    public required TestSecrets Secrets { get; init; }

    public required string ConnectionString { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestIdentity> StartAsync(Containers containers, Action<TestIdentityOptions>? configure = null)
    {
        var options = new TestIdentityOptions();
        configure?.Invoke(options);
        var fresh = options.ConnectionString is null;
        var connectionString = options.ConnectionString ?? await containers.CreateDatabaseAsync();
        var secrets = TestSecrets.Create();
        // PostgreSQL keeps microseconds; Linux clocks have 100 ns ticks
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));
        var logs = new LogCapture();

        var config = new Dictionary<string, string?>(secrets.ToConfig())
        {
            ["Identity:Mode"] = options.Mode,
            ["Identity:Database"] = connectionString,
            ["Identity:Redis"] = containers.RedisConnectionString,
            ["Identity:RabbitMq"] = containers.RabbitMqUri,
        };
        foreach (var (key, value) in options.Config) config[key] = value;

        var app = IdentityApp.Build([], b =>
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
        return new TestIdentity(app)
        {
            Http = app.GetTestClient(),
            Clock = clock,
            Logs = logs,
            Secrets = secrets,
            ConnectionString = connectionString,
        };
    }

    /// <summary>A test-side context on the same database.</summary>
    public IdentityDbContext NewDb() => NewDb(ConnectionString);

    /// <summary>The database's <c>now()</c>: the clock stored timestamps and time checks use, which the fake clock does not drive.</summary>
    public async Task<DateTimeOffset> DbNowAsync()
    {
        await using var db = NewDb();
        return await db.Database.NowAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The stored account, read on a fresh context.</summary>
    public async Task<Account> AccountAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id, TestContext.Current.CancellationToken);
    }

    private static IdentityDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.DisposeAsync();
    }
}
