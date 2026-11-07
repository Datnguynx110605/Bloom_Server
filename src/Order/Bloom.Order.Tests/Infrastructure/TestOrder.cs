using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Order.Tests;

public sealed class TestOrderOptions
{
    public string Mode { get; set; } = "api";

    /// <summary>Reuses this database (for example an API host and a worker host on one database) instead of creating one.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Config keys (for example <c>Order:TaxRateBasisPoints</c>) that replace the defaults of <see cref="TestOrder.DefaultConfig"/>.</summary>
    public Dictionary<string, string?> Config { get; } = [];
}

/// <summary>The order app built through <see cref="OrderApp.Build"/> on <c>TestServer</c> (plan R1), on its own database (plan R16).</summary>
public sealed class TestOrder : IAsyncDisposable
{
    private readonly WebApplication _app;

    private readonly GrpcChannel _channel;

    private TestOrder(WebApplication app, GrpcChannel channel)
    {
        _app = app;
        _channel = channel;
        Rpc = new RpcClients(channel);
    }

    /// <summary>A gRPC client per service of the app, called in-process (plan R1). Adding a service is one more property here.</summary>
    public sealed class RpcClients(GrpcChannel channel)
    {
        public OrderInternal.OrderInternalClient Internal { get; } = new(channel);
    }

    public RpcClients Rpc { get; }

    public required HttpClient Http { get; init; }

    /// <summary>The app's <see cref="TimeProvider"/>. Starts at the real time.</summary>
    public required FakeTimeProvider Clock { get; init; }

    public required LogCapture Logs { get; init; }

    /// <summary>The doubles of catalog, inventory, launch, payment and fulfillment; one per host.</summary>
    public required FakeUpstreams Upstreams { get; init; }

    /// <summary>The connection string of the database (the app connects as its owner, plan R2).</summary>
    public required string ConnectionString { get; init; }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestOrder> StartAsync(Containers containers, Action<TestOrderOptions>? configure = null)
    {
        var options = new TestOrderOptions();
        configure?.Invoke(options);
        var fresh = options.ConnectionString is null;
        var connectionString = options.ConnectionString ?? await containers.CreateDatabaseAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new LogCapture();
        var upstreams = await FakeUpstreams.StartAsync(clock);

        var config = DefaultConfig(connectionString, containers.RabbitMqUri, upstreams.Address.ToString());
        config["Order:Mode"] = options.Mode;
        foreach (var (key, value) in options.Config) config[key] = value;

        var app = OrderApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(config);
            b.Services.AddSingleton<TimeProvider>(clock); // registered first, so the app's TryAdd keeps it
            b.Services.AddSingleton<ILoggerProvider>(logs);
        });
        if (fresh)
        {
            await using var db = NewDb(connectionString);
            await db.Database.MigrateAsync(); // the app never migrates (plan R2)
        }
        await app.StartAsync();
        var server = app.GetTestServer();
        return new TestOrder(app, GrpcChannel.ForAddress(server.BaseAddress, new() { HttpHandler = server.CreateHandler() }))
        {
            Http = app.GetTestClient(),
            Clock = clock,
            Logs = logs,
            Upstreams = upstreams,
            ConnectionString = connectionString,
        };
    }

    /// <summary>Every key of plan R4 except <c>Mode</c>, with valid values; all five upstreams answer at <paramref name="upstreams"/>.</summary>
    internal static Dictionary<string, string?> DefaultConfig(string database, string rabbitMq, string upstreams) => new()
    {
        ["Order:Database"] = database,
        ["Order:RabbitMq"] = rabbitMq,
        ["Order:Catalog"] = upstreams,
        ["Order:Inventory"] = upstreams,
        ["Order:Launch"] = upstreams,
        ["Order:Payment"] = upstreams,
        ["Order:Fulfillment"] = upstreams,
        ["Order:TaxRateBasisPoints"] = "825",
    };

    /// <summary>A test-side context on the same database.</summary>
    public OrderDbContext NewDb() => NewDb(ConnectionString);

    private static OrderDbContext NewDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(connectionString).Options);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        _channel.Dispose();
        await _app.DisposeAsync();
        await Upstreams.DisposeAsync();
    }
}
