using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bloom.Shared.Messaging;
using Bloom.Shared.Observability;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Bloom.Shared.Tests;

public sealed class HandlerLog
{
    public ConcurrentQueue<Guid> Calls { get; } = new();
    public ConcurrentQueue<string?> Traces { get; } = new();
    public Func<Exception> Failure { get; set; } = () => new InvalidOperationException("transient");
}

public class RecordingHandler(TestDbContext db, HandlerLog log) : IMessageHandler
{
    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        log.Calls.Enqueue(envelope.MessageId);
        db.Effects.Add(new Effect { Consumer = GetType().Name, MessageId = envelope.MessageId });
        return Task.CompletedTask;
    }
}

public sealed class SecondRecordingHandler(TestDbContext db, HandlerLog log) : RecordingHandler(db, log);

public sealed class FailingHandler(HandlerLog log) : IMessageHandler
{
    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        log.Calls.Enqueue(envelope.MessageId);
        throw log.Failure();
    }
}

public sealed class PermanentHandler(HandlerLog log) : IMessageHandler
{
    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        log.Calls.Enqueue(envelope.MessageId);
        throw new PermanentFailureException("bad input");
    }
}

public sealed record RequiredPayload(string Name);

public sealed class PayloadHandler(HandlerLog log) : IMessageHandler
{
    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        log.Calls.Enqueue(envelope.MessageId);
        envelope.GetPayload<RequiredPayload>();
        return Task.CompletedTask;
    }
}

public sealed class OutboxHandler(OutboxWriter writer, BloomOptions options) : IMessageHandler
{
    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        writer.PublishEvent($"{options.ServiceName}.derived.v1", new { });
        return Task.CompletedTask;
    }
}

public sealed class TraceHandler(HandlerLog log) : IMessageHandler
{
    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        log.Traces.Enqueue(Activity.Current?.TraceId.ToString());
        return Task.CompletedTask;
    }
}

public sealed class LoggingHandler(ILogger<LoggingHandler> logger) : IMessageHandler
{
    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        logger.LogInformation("handled");
        return Task.CompletedTask;
    }
}

/// <summary>Records the scope values active when <see cref="LoggingHandler"/> logs.</summary>
public sealed class ScopeCapture : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopes;
    public ConcurrentQueue<Dictionary<string, object?>> Entries { get; } = new();

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Capture(ScopeCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!category.EndsWith(nameof(LoggingHandler))) return;
            var entry = new Dictionary<string, object?>();
            owner._scopes?.ForEachScope((scope, e) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var (k, v) in pairs) e[k] = v;
            }, entry);
            owner.Entries.Enqueue(entry);
        }
    }
}

public class ConsumerHostTests(Containers containers)
{
    private sealed record App(IHost Host, string Svc, HandlerLog Log, Rabbit Rabbit, TestDbContext Db) : IAsyncDisposable
    {
        public string Type(string name = "thing") => $"{Svc}.{name}.v1";

        public string Queue(string handler = "h") => $"{Svc}.{handler}";

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            Host.Dispose();
            await Rabbit.DisposeAsync();
        }
    }

    private async Task<App> StartAsync(Action<BloomBuilder, string> configure)
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new HandlerLog();
        var svc = "";
        var host = await TestApp.BuildAsync(containers, b =>
        {
            svc = ((BloomOptions)b.Services.Single(d => d.ServiceType == typeof(BloomOptions)).ImplementationInstance!).ServiceName;
            b.Services.AddSingleton(log);
            configure(b, svc);
            b.AddWorker();
        });
        await host.StartAsync(ct);
        var scope = host.Services.CreateScope(); // lives as long as the test host
        return new App(host, svc, log, await Rabbit.ConnectAsync(containers), scope.ServiceProvider.GetRequiredService<TestDbContext>());
    }

    private Task<App> StartOneAsync<THandler>() where THandler : class, IMessageHandler =>
        StartAsync((b, svc) => b.AddConsumer<THandler>("h", ConsumerBinding.Event(svc, $"{svc}.thing.v1")));

    private static byte[] Body(string type, string payload = "{}", Guid? id = null) =>
        new Envelope(id ?? Guid.NewGuid(), type, "event", "p", DateTime.UtcNow, Guid.NewGuid(),
            JsonDocument.Parse(payload).RootElement.Clone()).ToUtf8Bytes();

    private static async Task<Guid> WriteAsync(IHost host, string type)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = host.Services.CreateScope();
        var id = scope.ServiceProvider.GetRequiredService<OutboxWriter>().PublishEvent(type, new { });
        await scope.ServiceProvider.GetRequiredService<TestDbContext>().SaveChangesAsync(ct);
        return id;
    }

    private static async Task<BasicGetResult> GetWithinAsync(Rabbit rabbit, string queue, int seconds)
    {
        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await rabbit.BasicGetAsync(queue)) is not null, seconds, $"a message in {queue}");
        return got!;
    }

    private static string Header(BasicGetResult m, string name) => Encoding.UTF8.GetString((byte[])m.BasicProperties.Headers![name]!);

    private async Task<int> EffectsAsync(App a, string? consumer = null) =>
        await a.Db.Effects.AsNoTracking().CountAsync(e => consumer == null || e.Consumer == consumer, TestContext.Current.CancellationToken);

    private static async Task<List<(string Consumer, Guid MessageId)>> InboxAsync(App a) =>
        (await a.Db.Set<InboxMessage>().AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
        .Select(i => (i.Consumer, i.MessageId)).ToList();

    /// <summary>Stops the host so unacked deliveries go back to the queue, then checks the queue is empty (the original was ACKed).</summary>
    private static async Task AssertAckedAsync(App a, string queue)
    {
        await a.Host.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0u, await a.Rabbit.MessageCountAsync(queue));
    }

    [Fact]
    public async Task T05_duplicate_delivery_applies_once()
    {
        await using var app = await StartOneAsync<RecordingHandler>();
        using var capture = new MetricCapture(app.Svc);
        var body = Body(app.Type());

        await app.Rabbit.PublishAsync("", app.Queue(), body);
        await app.Rabbit.PublishAsync("", app.Queue(), body);

        await Poll.UntilAsync(() => capture.Sum("consumer_messages_total", "result", "duplicate") == 1, 10, "duplicate counted");
        Assert.Equal(1, await EffectsAsync(app));
        Assert.Equal(1, capture.Sum("consumer_messages_total", "result", "success"));
    }

    [Fact]
    public async Task T06_two_handlers_each_apply_once()
    {
        await using var app = await StartAsync((b, svc) => b
            .AddConsumer<RecordingHandler>("h1", ConsumerBinding.Event(svc, $"{svc}.thing.v1"))
            .AddConsumer<SecondRecordingHandler>("h2", ConsumerBinding.Event(svc, $"{svc}.thing.v1")));

        var id = await WriteAsync(app.Host, app.Type());

        await Poll.UntilAsync(async () => await EffectsAsync(app) == 2, 10, "both handlers applied");
        Assert.Equal(1, await EffectsAsync(app, nameof(RecordingHandler)));
        Assert.Equal(1, await EffectsAsync(app, nameof(SecondRecordingHandler)));
        Assert.Equal([(app.Queue("h1"), id), (app.Queue("h2"), id)], (await InboxAsync(app)).OrderBy(i => i.Consumer).ToList());
    }

    [Fact]
    public async Task T07_retry_routing_per_attempt()
    {
        await using var app = await StartOneAsync<FailingHandler>();
        var cases = new (int Attempt, string Queue, int Next)[] { (2, "retry.5s", 3), (3, "retry.30s", 4), (4, "retry.2m", 5) };

        foreach (var (attempt, suffix, next) in cases)
        {
            await app.Rabbit.PublishAsync("", app.Queue(), Body(app.Type()), new Dictionary<string, object?> { ["x-attempt"] = attempt });
            var got = await GetWithinAsync(app.Rabbit, $"{app.Queue()}.{suffix}", 5);
            Assert.Equal(next, Convert.ToInt32(got.BasicProperties.Headers!["x-attempt"]));
            Assert.Equal(DeliveryModes.Persistent, got.BasicProperties.DeliveryMode);
        }

        await app.Rabbit.PublishAsync("", app.Queue(), Body(app.Type()), new Dictionary<string, object?> { ["x-attempt"] = 5 });
        var dead = await GetWithinAsync(app.Rabbit, $"{app.Queue()}.dlq", 5);
        Assert.StartsWith("InvalidOperationException", Header(dead, "x-failure-reason"));
    }

    [Fact]
    public async Task T07_real_round_trip_through_1s_retry_queue()
    {
        await using var app = await StartOneAsync<FailingHandler>();

        await WriteAsync(app.Host, app.Type());

        await Poll.UntilAsync(() => app.Log.Calls.Count == 2, 10, "second attempt after the 1 s retry queue");
        var got = await GetWithinAsync(app.Rabbit, $"{app.Queue()}.retry.5s", 5);
        Assert.Equal(3, Convert.ToInt32(got.BasicProperties.Headers!["x-attempt"]));
        Assert.Equal(2, app.Log.Calls.Count);
    }

    [Fact]
    public async Task T08_permanent_failure_goes_straight_to_dlq()
    {
        await using var app = await StartOneAsync<PermanentHandler>();
        using var capture = new MetricCapture(app.Svc);

        var id = await WriteAsync(app.Host, app.Type());

        var dead = await GetWithinAsync(app.Rabbit, $"{app.Queue()}.dlq", 10);
        Assert.Equal(id.ToString(), dead.BasicProperties.MessageId);
        Assert.StartsWith("PermanentFailureException: bad input", Header(dead, "x-failure-reason"));
        var failedAt = Header(dead, "x-failed-at");
        Assert.EndsWith("Z", failedAt);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(failedAt, CultureInfo.InvariantCulture).Offset);
        await Poll.UntilAsync(() => capture.Sum("consumer_messages_total", "result", "dlq") == 1, 5, "dlq counted");
        Assert.Single(app.Log.Calls);
        foreach (var (delay, _) in MessageNames.RetryDelays)
            Assert.Equal(0u, await app.Rabbit.MessageCountAsync(MessageNames.RetryQueue(app.Queue(), delay)));
        Assert.Empty(await InboxAsync(app));
        await AssertAckedAsync(app, app.Queue());
    }

    [Fact]
    public async Task Invalid_envelope_goes_to_dlq()
    {
        await using var app = await StartOneAsync<RecordingHandler>();
        var noMessageId = Encoding.UTF8.GetBytes(
            $$$"""{"messageType":"{{{app.Type()}}}","kind":"event","producer":"p","occurredAt":"2026-01-01T00:00:00Z","correlationId":"{{{Guid.NewGuid()}}}","payload":{}}""");

        await app.Rabbit.PublishAsync("", app.Queue(), Encoding.UTF8.GetBytes("not json"));
        await app.Rabbit.PublishAsync("", app.Queue(), noMessageId);

        for (var i = 0; i < 2; i++)
            Assert.StartsWith("invalid envelope: ", Header(await GetWithinAsync(app.Rabbit, $"{app.Queue()}.dlq", 10), "x-failure-reason"));
        Assert.Empty(app.Log.Calls);
        Assert.Empty(await InboxAsync(app));
        await AssertAckedAsync(app, app.Queue());
    }

    [Fact]
    public async Task Unknown_message_type_goes_to_dlq()
    {
        await using var app = await StartOneAsync<RecordingHandler>();

        await app.Rabbit.PublishAsync("", app.Queue(), Body(app.Type("other")));

        var dead = await GetWithinAsync(app.Rabbit, $"{app.Queue()}.dlq", 10);
        Assert.Equal($"unknown message type: {app.Type("other")}", Header(dead, "x-failure-reason"));
        Assert.Empty(app.Log.Calls);
        Assert.Empty(await InboxAsync(app));
        await AssertAckedAsync(app, app.Queue());
    }

    [Fact]
    public async Task Payload_missing_required_field_goes_to_dlq()
    {
        await using var app = await StartOneAsync<PayloadHandler>();

        await app.Rabbit.PublishAsync("", app.Queue(), Body(app.Type(), "{}"));

        var dead = await GetWithinAsync(app.Rabbit, $"{app.Queue()}.dlq", 10);
        Assert.StartsWith("PermanentFailureException", Header(dead, "x-failure-reason"));
        Assert.Single(app.Log.Calls);
        Assert.Empty(await InboxAsync(app));
        await AssertAckedAsync(app, app.Queue());
    }

    [Fact]
    public async Task Handler_outbox_writes_commit_with_inbox()
    {
        await using var app = await StartOneAsync<OutboxHandler>();

        var id = await WriteAsync(app.Host, app.Type());

        await Poll.UntilAsync(async () => await app.Db.Set<OutboxMessage>().AsNoTracking()
            .AnyAsync(o => o.MessageType == app.Type("derived"), TestContext.Current.CancellationToken), 10, "derived outbox row");
        var derived = await app.Db.Set<OutboxMessage>().AsNoTracking().SingleAsync(o => o.MessageType == app.Type("derived"), TestContext.Current.CancellationToken);
        Assert.Equal(id.ToString(), JsonSerializer.Deserialize<Dictionary<string, string>>(derived.Headers)!["causation_id"]);
        Assert.Equal(id, JsonDocument.Parse(derived.Payload).RootElement.GetProperty("causationId").GetGuid());
        Assert.Equal([(app.Queue(), id)], await InboxAsync(app));
    }

    [Fact]
    public async Task Consumer_continues_trace_from_traceparent()
    {
        await using var app = await StartOneAsync<TraceHandler>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Bloom.Shared",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        string? traceId;
        using (var activity = BloomActivity.Source.StartActivity("test"))
        {
            traceId = activity?.TraceId.ToString();
            await WriteAsync(app.Host, app.Type());
        }
        Assert.NotNull(traceId);

        await Poll.UntilAsync(() => !app.Log.Traces.IsEmpty, 10, "handler ran");

        Assert.Equal(traceId, app.Log.Traces.Single());
    }

    [Fact]
    public async Task Log_scope_carries_message_fields()
    {
        var scopes = new ScopeCapture();
        await using var app = await StartAsync((b, svc) =>
        {
            b.Services.AddSingleton<ILoggerProvider>(scopes);
            b.AddConsumer<LoggingHandler>("h", ConsumerBinding.Event(svc, $"{svc}.thing.v1"));
        });

        var id = await WriteAsync(app.Host, app.Type());

        await Poll.UntilAsync(() => !scopes.Entries.IsEmpty, 10, "handler logged");
        var scope = scopes.Entries.Single();
        Assert.Equal(id, scope["message_id"]);
        Assert.Equal(app.Type(), scope["message_type"]);
        Assert.NotNull(scope["correlation_id"]);
        Assert.Equal(app.Queue(), scope["consumer"]);
    }

    [Fact]
    public async Task Reconnects_after_connection_drop()
    {
        await using var app = await StartOneAsync<RecordingHandler>();
        await WriteAsync(app.Host, app.Type());
        await Poll.UntilAsync(async () => await EffectsAsync(app) == 1, 10, "first message processed");

        // Only this service's connection (named after it), so tests sharing the broker are not disturbed.
        var listing = await containers.RabbitCtlAsync("list_connections", "pid", "client_properties");
        var pid = listing.Split('\n').Single(l => l.Contains(app.Svc)).Split('\t')[0];
        await containers.RabbitCtlAsync("close_connection", pid, "test");
        await WriteAsync(app.Host, app.Type());

        await Poll.UntilAsync(async () => await EffectsAsync(app) == 2, 15, "second message processed after the drop");
    }
}
