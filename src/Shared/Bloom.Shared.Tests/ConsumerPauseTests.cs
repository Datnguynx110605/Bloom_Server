using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;

namespace Bloom.Shared.Tests;

/// <summary>
/// D3 / HLD-ADR-019. Pausing the shared Postgres container would break tests running in parallel,
/// so this class owns a dedicated one.
/// </summary>
public class ConsumerPauseTests(Containers containers) : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").Build();
    private bool _paused;

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_paused) await _postgres.UnpauseAsync();
        await _postgres.DisposeAsync();
    }

    private async Task PauseAsync()
    {
        _paused = true;
        await _postgres.PauseAsync(TestContext.Current.CancellationToken);
    }

    private async Task UnpauseAsync()
    {
        await _postgres.UnpauseAsync(TestContext.Current.CancellationToken);
        _paused = false;
    }

    private sealed record App(IHost Host, string Svc, HandlerLog Log, Rabbit Rabbit, TestDbContext Db) : IAsyncDisposable
    {
        public string Queue => $"{Svc}.h";

        public string Type => $"{Svc}.thing.v1";

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
            await Rabbit.DisposeAsync();
        }
    }

    private async Task<App> StartAsync<THandler>() where THandler : class, IMessageHandler
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"t_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await admin.OpenAsync(ct);
            await new NpgsqlCommand($"CREATE DATABASE {name}", admin).ExecuteNonQueryAsync(ct);
        }
        // Timeout / Command Timeout 2 s: a paused server fails fast instead of hanging for the 15 s / 30 s defaults.
        var cs = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name, Timeout = 2, CommandTimeout = 2 }.ConnectionString;

        var log = new HandlerLog();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<TestDbContext>(o => o.UseNpgsql(cs));
        var svc = "t" + Guid.NewGuid().ToString("N")[..8];
        var bloom = builder.Services.AddBloom<TestDbContext>(o =>
        {
            o.ServiceName = svc;
            o.RabbitMqUri = containers.RabbitMqUri;
        });
        builder.Services.AddSingleton(log);
        bloom.AddConsumer<THandler>("h", ConsumerBinding.Event(svc, $"{svc}.thing.v1"));
        bloom.AddWorker();
        var host = builder.Build();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync(ct);
        await host.StartAsync(ct);
        var dbScope = host.Services.CreateScope(); // lives as long as the test host
        return new App(host, svc, log, await Rabbit.ConnectAsync(containers), dbScope.ServiceProvider.GetRequiredService<TestDbContext>());
    }

    private static byte[] Body(string type) =>
        new Envelope(Guid.NewGuid(), type, "event", "p", DateTime.UtcNow, Guid.NewGuid(), JsonDocument.Parse("{}").RootElement.Clone()).ToUtf8Bytes();

    private static async Task<uint> ConsumersAsync(App a) => (await a.Rabbit.Channel.QueueDeclarePassiveAsync(a.Queue)).ConsumerCount;

    private static async Task<BasicGetResult> GetWithinAsync(Rabbit rabbit, string queue, int seconds)
    {
        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await rabbit.BasicGetAsync(queue)) is not null, seconds, $"a message in {queue}");
        return got!;
    }

    private static string RetryQueue(App a, string delay) => MessageNames.RetryQueue(a.Queue, delay);

    [Fact]
    public void IsOutage_classifies()
    {
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        var cases = new (string Name, Exception Ex, bool Expected)[]
        {
            ("connection refused", new NpgsqlException("refused", refused), true),
            ("connect timeout", new NpgsqlException("timeout", new TimeoutException()), true),
            ("connection reset", new NpgsqlException("reset", new IOException("reset", refused)), true),
            ("57P01 admin shutdown", new PostgresException("m", "FATAL", "FATAL", "57P01"), true),
            ("57P03 not accepting", new PostgresException("m", "FATAL", "FATAL", "57P03"), true),
            ("wrapped by EF", new InvalidOperationException("retry", new NpgsqlException("refused", refused)), true),
            ("23505 unique violation", new PostgresException("m", "ERROR", "ERROR", "23505"), false),
            ("57014 statement timeout", new PostgresException("m", "ERROR", "ERROR", "57014"), false),
            ("40001 serialization failure", new PostgresException("m", "ERROR", "ERROR", "40001"), false),
            ("plain NpgsqlException", new NpgsqlException("bad"), false),
            ("InvalidOperationException", new InvalidOperationException("x"), false),
            ("bare socket error", new SocketException(), false),
            ("PermanentFailureException", new PermanentFailureException("bad input"), false),
        };

        foreach (var (name, ex, expected) in cases)
            Assert.True(expected == DatabaseOutage.IsOutage(ex), $"{name}: expected {expected}");
    }

    [Fact]
    public async Task Db_paused_messages_are_not_counted_or_dead_lettered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync<RecordingHandler>();
        using var capture = new MetricCapture(app.Svc);

        await PauseAsync();
        for (var i = 0; i < 3; i++) await app.Rabbit.PublishAsync("", app.Queue, Body(app.Type));
        // Staying paused until the host noticed (about 6 s: command timeout plus Npgsql's cancel wait) makes the outage deterministic.
        await Poll.UntilAsync(async () => await ConsumersAsync(app) == 0, 60, "consumer cancelled during the outage");
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await UnpauseAsync();

        await Poll.UntilAsync(async () => await app.Db.Effects.AsNoTracking().CountAsync(ct) == 3, 60, "all 3 messages applied after recovery");
        Assert.Equal(3, app.Log.Calls.Distinct().Count());
        // Retried messages would be back in the main queue by now, so check the counters, not the queue depths.
        Assert.Equal(0, capture.Sum("consumer_messages_total", "result", "retry"));
        Assert.Equal(0, capture.Sum("consumer_messages_total", "result", "dlq"));
        foreach (var (delay, _) in MessageNames.RetryDelays)
            Assert.Equal(0u, await app.Rabbit.MessageCountAsync(RetryQueue(app, delay)));
        Assert.Equal(0u, await app.Rabbit.MessageCountAsync(MessageNames.DeadLetterQueue(app.Queue)));
    }

    [Fact]
    public async Task Consuming_stops_while_db_is_down()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync<RecordingHandler>();
        Assert.Equal(1u, await ConsumersAsync(app));

        await PauseAsync();
        await app.Rabbit.PublishAsync("", app.Queue, Body(app.Type));
        await Poll.UntilAsync(async () => await ConsumersAsync(app) == 0, 60, "consumer cancelled during the outage");
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.Equal(0u, await ConsumersAsync(app));
        Assert.Equal(1u, await app.Rabbit.MessageCountAsync(app.Queue));
        Assert.Empty(app.Log.Calls);

        await UnpauseAsync();
        await Poll.UntilAsync(async () => await ConsumersAsync(app) == 1, 10, "consuming resumed after the database came back");
        await Poll.UntilAsync(() => app.Log.Calls.Count == 1, 10, "the waiting message handled");
    }

    [Fact]
    public async Task Handler_exception_keeps_five_attempt_rule()
    {
        await using var app = await StartAsync<FailingHandler>();

        await app.Rabbit.PublishAsync("", app.Queue, Body(app.Type), new Dictionary<string, object?> { ["x-attempt"] = 5 });

        var dead = await GetWithinAsync(app.Rabbit, MessageNames.DeadLetterQueue(app.Queue), 10);
        Assert.StartsWith("InvalidOperationException", Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers!["x-failure-reason"]!));
        Assert.Single(app.Log.Calls);
        Assert.Equal(1u, await ConsumersAsync(app));
    }

    [Fact]
    public async Task Handler_postgres_error_is_counted()
    {
        await using var app = await StartAsync<FailingHandler>();

        foreach (var sqlState in new[] { "23505", "57014", "40001" })
        {
            app.Log.Failure = () => new PostgresException("m", "ERROR", "ERROR", sqlState);
            await app.Rabbit.PublishAsync("", app.Queue, Body(app.Type), new Dictionary<string, object?> { ["x-attempt"] = 2 });
            var retried = await GetWithinAsync(app.Rabbit, RetryQueue(app, "5s"), 10);
            Assert.Equal(3, Convert.ToInt32(retried.BasicProperties.Headers!["x-attempt"]));
        }
        await app.Rabbit.PublishAsync("", app.Queue, Body(app.Type), new Dictionary<string, object?> { ["x-attempt"] = 5 });
        var dead = await GetWithinAsync(app.Rabbit, MessageNames.DeadLetterQueue(app.Queue), 10);
        Assert.StartsWith("PostgresException", Encoding.UTF8.GetString((byte[])dead.BasicProperties.Headers!["x-failure-reason"]!));
        Assert.Equal(1u, await ConsumersAsync(app));
    }
}
