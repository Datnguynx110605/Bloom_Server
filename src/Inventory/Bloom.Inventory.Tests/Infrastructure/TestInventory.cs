using Bloom.Inventory.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Inventory.Tests;

public sealed class TestInventoryOptions
{
    public string Mode { get; set; } = "api";

    /// <summary>Reuses this database (the owner connection string, for example an API host and a worker host on one database) instead of creating one.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Configuration overrides, applied on top of the real appsettings.json.</summary>
    public Dictionary<string, string?> Config { get; } = [];
}

/// <summary>The inventory app built through <see cref="InventoryApp.Build"/> on <c>TestServer</c> (plan R1), on its own database, as the app role (plan R8), with its own <see cref="FakeCatalog"/>.</summary>
public sealed class TestInventory : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestInventory(WebApplication app) => _app = app;

    public required HttpClient Http { get; init; }

    public required FakeCatalog Catalog { get; init; }

    /// <summary>The app's <see cref="TimeProvider"/>. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    /// <summary>The database owner's connection string; the app itself connects as the app role.</summary>
    public required string ConnectionString { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestInventory> StartAsync(Containers containers, Action<TestInventoryOptions>? configure = null)
    {
        var options = new TestInventoryOptions();
        configure?.Invoke(options);
        var fresh = options.ConnectionString is null;
        var connectionString = options.ConnectionString ?? await containers.CreateDatabaseAsync();
        var catalog = await FakeCatalog.StartAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new LogCapture();

        var config = new Dictionary<string, string?>
        {
            ["Inventory:Mode"] = options.Mode,
            ["Inventory:Database"] = Containers.AsAppRole(connectionString),
            ["Inventory:RabbitMq"] = containers.RabbitMqUri,
            ["Inventory:Catalog"] = catalog.Address.ToString(),
        };
        foreach (var (key, value) in options.Config) config[key] = value;

        var app = InventoryApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(clock); // registered first, so the app's TryAdd keeps it
            b.Services.AddSingleton<ILoggerProvider>(logs);
        });
        if (fresh)
        {
            await using var db = NewDb(connectionString);
            await db.Database.MigrateAsync(); // as the database owner (plan R2)
        }
        await app.StartAsync();
        return new TestInventory(app)
        {
            Http = app.GetTestClient(),
            Catalog = catalog,
            Clock = clock,
            Logs = logs,
            ConnectionString = connectionString,
        };
    }

    /// <summary>A test-side context on the same database, as the owner.</summary>
    public InventoryDbContext NewDb() => NewDb(ConnectionString);

    private static InventoryDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(connectionString).Options);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.DisposeAsync();
        await Catalog.DisposeAsync();
    }
}
