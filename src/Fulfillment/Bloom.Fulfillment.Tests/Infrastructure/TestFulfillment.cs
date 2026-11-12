using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Shippo;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Fulfillment.Tests;

public sealed class TestFulfillmentOptions
{
    public string Mode { get; set; } = "api";

    /// <summary>Reuses this database (for example an API host and a worker host on one database) instead of creating one.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Extra services of the app (for example a DbContext interceptor), registered before the app's own.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

/// <summary>The fulfillment app built through <see cref="FulfillmentApp.Build"/> on <c>TestServer</c> (plan R1), on its own database (plan R23).</summary>
public sealed class TestFulfillment : IAsyncDisposable
{
    private readonly WebApplication _app;

    private readonly GrpcChannel _channel;

    private TestFulfillment(WebApplication app, GrpcChannel channel)
    {
        _app = app;
        _channel = channel;
        Internal = new FulfillmentInternal.FulfillmentInternalClient(channel);
    }

    /// <summary>The internal gRPC API, called in-process (plan R1).</summary>
    public FulfillmentInternal.FulfillmentInternalClient Internal { get; }

    public required HttpClient Http { get; init; }

    /// <summary>The app's <see cref="TimeProvider"/>. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    /// <summary>The inventory double the app calls; one per host.</summary>
    public required FakeInventory Inventory { get; init; }

    /// <summary>The Shippo double behind the app's real <see cref="ShippoClient"/>; one per host.</summary>
    public required FakeShippo Shippo { get; init; }

    /// <summary>The connection string of the database (the app connects as its owner, plan R2).</summary>
    public required string ConnectionString { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestFulfillment> StartAsync(Containers containers, Action<TestFulfillmentOptions>? configure = null)
    {
        var options = new TestFulfillmentOptions();
        configure?.Invoke(options);
        var fresh = options.ConnectionString is null;
        var connectionString = options.ConnectionString ?? await containers.CreateDatabaseAsync();
        // PostgreSQL keeps microseconds; Linux clocks have 100 ns ticks
        var clock = new FakeTimeProvider(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));
        var logs = new LogCapture();
        var inventory = await FakeInventory.StartAsync();
        var shippo = new FakeShippo();

        var config = DefaultConfig(connectionString, containers.RabbitMqUri, inventory.Address.ToString());
        config["Fulfillment:Mode"] = options.Mode;

        var app = FulfillmentApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(clock); // registered first, so the app's TryAdd keeps it
            b.Services.AddSingleton<ILoggerProvider>(logs);
            options.ConfigureServices?.Invoke(b.Services);
            b.Services.AddHttpClient<IShippo, ShippoClient>().ConfigurePrimaryHttpMessageHandler(() => shippo); // the app's own registration adds the rest
        });
        if (fresh)
        {
            await using var db = NewDb(connectionString);
            await db.Database.MigrateAsync(); // the app never migrates (plan R2)
        }
        await app.StartAsync();
        var server = app.GetTestServer();
        return new TestFulfillment(app, GrpcChannel.ForAddress(server.BaseAddress, new() { HttpHandler = server.CreateHandler() }))
        {
            Http = app.GetTestClient(),
            Clock = clock,
            Logs = logs,
            Inventory = inventory,
            Shippo = shippo,
            ConnectionString = connectionString,
        };
    }

    /// <summary>Every key of plan R4 except <c>Mode</c>, with valid values.</summary>
    internal static Dictionary<string, string?> DefaultConfig(string database, string rabbitMq, string inventory) => new()
    {
        ["Fulfillment:Database"] = database,
        ["Fulfillment:RabbitMq"] = rabbitMq,
        ["Fulfillment:Inventory"] = inventory,
        ["Fulfillment:Shippo:ApiKey"] = "key-test",
        ["Fulfillment:Shippo:WebhookSecret"] = "secret-test",
        ["Fulfillment:Shippo:Carrier"] = "usps",
        ["Fulfillment:Shippo:StandardToken"] = "usps_ground_advantage",
        ["Fulfillment:Shippo:ExpressToken"] = "usps_priority",
        ["Fulfillment:Warehouse:Name"] = "Bloom Warehouse",
        ["Fulfillment:Warehouse:Street1"] = "1 Warehouse Way",
        ["Fulfillment:Warehouse:City"] = "Reno",
        ["Fulfillment:Warehouse:State"] = "NV",
        ["Fulfillment:Warehouse:Zip"] = "89501",
        ["Fulfillment:Warehouse:Phone"] = "+17755550100",
        ["Fulfillment:Parcel:LengthIn"] = "10",
        ["Fulfillment:Parcel:WidthIn"] = "8",
        ["Fulfillment:Parcel:HeightIn"] = "4",
        ["Fulfillment:Parcel:WeightOz"] = "32",
    };

    /// <summary>A test-side context on the same database.</summary>
    public FulfillmentDbContext NewDb() => NewDb(ConnectionString);

    private static FulfillmentDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<FulfillmentDbContext>().UseNpgsql(connectionString).Options);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        _channel.Dispose();
        await _app.DisposeAsync();
        await Inventory.DisposeAsync();
    }
}
