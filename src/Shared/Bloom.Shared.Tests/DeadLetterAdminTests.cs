using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Bloom.Shared.Tests;

public sealed class Toggle
{
    public volatile bool Fail;
}

/// <summary>Fails permanently while <see cref="Toggle.Fail"/> is set, otherwise records one effect.</summary>
public sealed class ToggleHandler(TestDbContext db, Toggle toggle) : IMessageHandler
{
    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        if (toggle.Fail) throw new PermanentFailureException("poison");
        db.Effects.Add(new Effect { Consumer = nameof(ToggleHandler), MessageId = envelope.MessageId });
        return Task.CompletedTask;
    }
}

/// <summary>Parks the first audit of a pass until released, so a test can hold an operation mid-pass.</summary>
internal sealed class GatedPass(RabbitMqConnection connection) : DeadLetterPass(connection)
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task RunAsync(
        string dlq, Func<BasicDeliverEventArgs, PassAction> decide, Func<BasicDeliverEventArgs, PassAction, Task> audit, CancellationToken ct) =>
        base.RunAsync(dlq, decide, async (d, action) =>
        {
            Entered.TrySetResult();
            await Release.Task;
            await audit(d, action);
        }, ct);
}

/// <summary>Throws on its first pass, then behaves normally.</summary>
internal sealed class ThrowOncePass(RabbitMqConnection connection) : DeadLetterPass(connection)
{
    private int _calls;

    public override Task RunAsync(
        string dlq, Func<BasicDeliverEventArgs, PassAction> decide, Func<BasicDeliverEventArgs, PassAction, Task> audit, CancellationToken ct) =>
        Interlocked.Increment(ref _calls) == 1 ? throw new IOException("pass failed") : base.RunAsync(dlq, decide, audit, ct);
}

public class DeadLetterAdminTests(Containers containers)
{
    private const string AuditType = "identity.staff-action-recorded.v1";

    private sealed record App(WebApplication Host, string Cs, string Svc, Toggle Toggle, Rabbit Rabbit, DeadLetterAdmin.DeadLetterAdminClient Client) : IAsyncDisposable
    {
        public string Type => $"{Svc}.thing.v1";

        public string Dlq => $"{Svc}.h.dlq";

        public string Queue => $"{Svc}.h";

        public readonly Guid Actor = Guid.NewGuid();

        public CallOptions Headers(string role = "admin", string userType = "staff") => new(
            new Metadata
            {
                { "x-bloom-user-type", userType },
                { "x-bloom-role", role },
                { "x-bloom-user-id", Actor.ToString() },
                { "x-bloom-request-id", Guid.NewGuid().ToString() },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Host.StopAsync();
            await Host.DisposeAsync();
            await Rabbit.DisposeAsync();
        }
    }

    private async Task<App> StartAsync(bool worker = false, Action<IServiceCollection>? configure = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await containers.CreateDatabaseAsync();
        var svc = "t" + Guid.NewGuid().ToString("N")[..8];
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TestDbContext>(o => o.UseNpgsql(cs));
        builder.Services.AddGrpc().AddJsonTranscoding();
        var toggle = new Toggle();
        builder.Services.AddSingleton(toggle);
        var bloom = builder.Services.AddBloom<TestDbContext>(o =>
        {
            o.ServiceName = svc;
            o.RabbitMqUri = containers.RabbitMqUri;
        }).AddConsumer<ToggleHandler>("h", ConsumerBinding.Event(svc, $"{svc}.thing.v1"));
        if (worker) bloom.AddWorker();
        else bloom.Services.AddHostedService<TopologyInitializer>(); // declares the queues, consumes nothing
        configure?.Invoke(builder.Services);
        var host = builder.Build();
        host.MapBloomDeadLetterAdmin();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync(ct);
        await host.StartAsync(ct);
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        return new App(host, cs, svc, toggle, await Rabbit.ConnectAsync(containers), new DeadLetterAdmin.DeadLetterAdminClient(channel));
    }

    private static byte[] Body(string type, Guid id, int n) =>
        new Envelope(id, type, "event", "p", DateTime.UtcNow, Guid.NewGuid(), JsonDocument.Parse($$"""{"n":{{n}}}""").RootElement.Clone()).ToUtf8Bytes();

    /// <summary>Publishes <paramref name="n"/> dead-letter-shaped messages straight to the DLQ and returns their ids in order.</summary>
    private static async Task<List<Guid>> SeedAsync(App a, int n, string? queue = null)
    {
        var ids = new List<Guid>();
        for (var i = 1; i <= n; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            await a.Rabbit.Channel.BasicPublishAsync("", queue ?? a.Dlq, false, new BasicProperties
            {
                MessageId = id.ToString(),
                Type = a.Type,
                DeliveryMode = DeliveryModes.Persistent,
                Headers = new Dictionary<string, object?>
                {
                    ["x-attempt"] = 5,
                    ["x-failure-reason"] = $"boom {i}",
                    ["x-failed-at"] = DateTime.UtcNow.ToString("O"),
                    ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
                },
            }, Body(a.Type, id, i));
        }
        return ids;
    }

    private static Task UntilCountAsync(App a, string queue, uint expected) =>
        Poll.UntilAsync(async () => await a.Rabbit.MessageCountAsync(queue) == expected, 10, $"{queue} to hold {expected}");

    private static async Task<List<Envelope>> AuditAsync(App a)
    {
        using var scope = a.Host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessage>().AsNoTracking()
                .Where(m => m.MessageType == AuditType).OrderBy(m => m.Id).ToListAsync(TestContext.Current.CancellationToken))
            .Select(m => Envelope.Parse(Encoding.UTF8.GetBytes(m.Payload))).ToList();
    }

    private static async Task AssertErrorAsync(StatusCode code, string reason, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, ex.StatusCode);
        Assert.Equal(reason, ex.GetRpcStatus()!.GetDetail<ErrorInfo>().Reason);
    }

    private static async Task<List<Guid>> DrainAsync(App a, string queue)
    {
        var ids = new List<Guid>();
        while (await a.Rabbit.BasicGetAsync(queue) is { } m) ids.Add(Guid.Parse(m.BasicProperties.MessageId!));
        return ids;
    }

    private Task<PeekDeadLettersResponse> PeekAsync(App a, int limit = 0) =>
        a.Client.PeekDeadLettersAsync(new PeekDeadLettersRequest { Service = a.Svc, Queue = a.Dlq, Limit = limit }, a.Headers()).ResponseAsync;

    [Fact]
    public async Task T09_replay_after_fix_processes_once_with_one_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(worker: true);
        app.Toggle.Fail = true;
        var id = Guid.CreateVersion7();
        await app.Rabbit.Channel.BasicPublishAsync($"{app.Svc}.events", app.Type, false,
            new BasicProperties { MessageId = id.ToString(), Type = app.Type, DeliveryMode = DeliveryModes.Persistent }, Body(app.Type, id, 1), ct);
        await UntilCountAsync(app, app.Dlq, 1);
        app.Toggle.Fail = false;

        await app.Client.ReplayDeadLetterAsync(new DeadLetterRequest { Service = app.Svc, Queue = app.Dlq, MessageId = id.ToString() }, app.Headers());

        using var scope = app.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        await Poll.UntilAsync(async () => await db.Effects.AsNoTracking().CountAsync(ct) == 1, 10, "one effect after replay");
        var audit = Assert.Single(await AuditAsync(app));
        var p = audit.Payload;
        Assert.Equal(app.Actor, p.GetProperty("actorId").GetGuid());
        Assert.Equal("admin", p.GetProperty("actorRole").GetString());
        Assert.Equal(app.Svc, p.GetProperty("service").GetString());
        Assert.Equal($"{app.Svc}.dlq-replayed", p.GetProperty("action").GetString());
        Assert.Equal("dlq_message", p.GetProperty("targetType").GetString());
        Assert.Equal(id.ToString(), p.GetProperty("targetId").GetString());
        Assert.Equal(app.Dlq, p.GetProperty("details").GetProperty("queue").GetString());
        Assert.Equal(app.Type, p.GetProperty("details").GetProperty("messageType").GetString());
        Assert.StartsWith("PermanentFailureException: poison", p.GetProperty("details").GetProperty("failureReason").GetString());
        await UntilCountAsync(app, app.Dlq, 0);
        Assert.Equal(1, await db.Effects.AsNoTracking().CountAsync(ct));
    }

    [Fact]
    public async Task T20_replay_all_three()
    {
        await using var app = await StartAsync();
        var ids = await SeedAsync(app, 3);

        var response = await app.Client.ReplayAllDeadLettersAsync(new ReplayAllDeadLettersRequest { Service = app.Svc, Queue = app.Dlq }, app.Headers());

        Assert.Equal(3u, response.ReplayedCount);
        var got = new List<BasicGetResult>();
        while (await app.Rabbit.BasicGetAsync(app.Queue) is { } m) got.Add(m);
        Assert.Equal(ids, got.Select(m => Guid.Parse(m.BasicProperties.MessageId!)).ToList());
        foreach (var m in got)
        {
            Assert.DoesNotContain("x-attempt", m.BasicProperties.Headers!.Keys);
            Assert.DoesNotContain("x-failure-reason", m.BasicProperties.Headers.Keys);
            Assert.DoesNotContain("x-failed-at", m.BasicProperties.Headers.Keys);
            Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", Encoding.UTF8.GetString((byte[])m.BasicProperties.Headers["traceparent"]!));
            Assert.Equal(DeliveryModes.Persistent, m.BasicProperties.DeliveryMode);
        }
        Assert.Equal(3, (await AuditAsync(app)).Count);
        await UntilCountAsync(app, app.Dlq, 0);
    }

    [Fact]
    public async Task T21_discard_middle_of_three()
    {
        await using var app = await StartAsync();
        var ids = await SeedAsync(app, 3);

        await app.Client.DiscardDeadLetterAsync(new DeadLetterRequest { Service = app.Svc, Queue = app.Dlq, MessageId = ids[1].ToString() }, app.Headers());

        Assert.Equal([ids[0].ToString(), ids[2].ToString()], (await PeekAsync(app)).Messages.Select(m => m.MessageId));
        var audit = Assert.Single(await AuditAsync(app));
        Assert.Equal($"{app.Svc}.dlq-discarded", audit.Payload.GetProperty("action").GetString());
        Assert.Equal(ids[1].ToString(), audit.Payload.GetProperty("targetId").GetString());
    }

    [Fact]
    public async Task T22_peek_twice_is_stable()
    {
        await using var app = await StartAsync();
        var ids = (await SeedAsync(app, 3)).Select(i => i.ToString()).ToList();

        var first = await PeekAsync(app);
        var second = await PeekAsync(app);

        Assert.Equal(ids, first.Messages.Select(m => m.MessageId));
        Assert.Equal(ids, second.Messages.Select(m => m.MessageId));
        Assert.Equal(["boom 1", "boom 2", "boom 3"], first.Messages.Select(m => m.FailureReason));
        Assert.All(first.Messages, m =>
        {
            Assert.NotEmpty(m.FailedAt);
            Assert.Equal(app.Type, m.MessageType);
            Assert.Equal(m.MessageId, JsonDocument.Parse(m.EnvelopeJson).RootElement.GetProperty("messageId").GetString());
        });
        await UntilCountAsync(app, app.Dlq, 3);
        Assert.Empty(await AuditAsync(app));
        Assert.Equal(ids.Take(2), (await PeekAsync(app, 2)).Messages.Select(m => m.MessageId));
        Assert.Equal(ids.Take(1), (await PeekAsync(app, 1)).Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task Peek_limit_is_clamped()
    {
        await using var app = await StartAsync();
        var ids = (await SeedAsync(app, 3)).Select(i => i.ToString()).ToList();

        Assert.Equal(ids, (await PeekAsync(app, 1000)).Messages.Select(m => m.MessageId));
        Assert.Single((await PeekAsync(app, -5)).Messages);
    }

    [Fact]
    public async Task T23_second_operation_gets_dlq_busy()
    {
        await using var app = await StartAsync(configure: s => s.AddSingleton<DeadLetterPass>(sp => new GatedPass(sp.GetRequiredService<RabbitMqConnection>())));
        var gate = (GatedPass)app.Host.Services.GetRequiredService<DeadLetterPass>();
        var ids = await SeedAsync(app, 3);
        var request = new ReplayAllDeadLettersRequest { Service = app.Svc, Queue = app.Dlq };
        var a = app.Client.ReplayAllDeadLettersAsync(request, app.Headers()).ResponseAsync; // parks after publishing the first message
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await AssertErrorAsync(StatusCode.Aborted, "DLQ_BUSY", () => app.Client.ReplayAllDeadLettersAsync(request, app.Headers()).ResponseAsync);
        await AssertErrorAsync(StatusCode.Aborted, "DLQ_BUSY", () => PeekAsync(app));
        await AssertErrorAsync(StatusCode.Aborted, "DLQ_BUSY",
            () => app.Client.DiscardDeadLetterAsync(new DeadLetterRequest { Service = app.Svc, Queue = app.Dlq, MessageId = ids[2].ToString() }, app.Headers()).ResponseAsync);

        Assert.Equal(1u, await app.Rabbit.MessageCountAsync(app.Queue)); // only A's first replay so far
        gate.Release.SetResult();
        Assert.Equal(3u, (await a).ReplayedCount);
        Assert.Equal(0u, (await app.Client.ReplayAllDeadLettersAsync(request, app.Headers())).ReplayedCount); // the lock is free again
        Assert.Equal(ids, await DrainAsync(app, app.Queue));
        Assert.Equal(3, (await AuditAsync(app)).Count);
    }

    [Fact]
    public async Task Two_simultaneous_replay_alls_one_wins_one_gets_dlq_busy()
    {
        await using var app = await StartAsync(configure: s => s.AddSingleton<DeadLetterPass>(sp => new GatedPass(sp.GetRequiredService<RabbitMqConnection>())));
        var gate = (GatedPass)app.Host.Services.GetRequiredService<DeadLetterPass>();
        var ids = await SeedAsync(app, 3);
        var request = new ReplayAllDeadLettersRequest { Service = app.Svc, Queue = app.Dlq };

        var calls = Enumerable.Range(0, 2).Select(_ => app.Client.ReplayAllDeadLettersAsync(request, app.Headers()).ResponseAsync).ToList();
        await Task.WhenAny(calls); // the loser fails fast on the lock while the winner is parked
        gate.Release.SetResult();
        var outcomes = await Task.WhenAll(calls.Select(async c =>
        {
            try { return (long)(await c).ReplayedCount; }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Aborted && ex.GetRpcStatus()!.GetDetail<ErrorInfo>().Reason == "DLQ_BUSY") { return -1L; }
        }));

        Assert.Equal([-1L, 3L], outcomes.Order());
        Assert.Equal(ids, await DrainAsync(app, app.Queue));
        Assert.Equal(3, (await AuditAsync(app)).Count);
        await UntilCountAsync(app, app.Dlq, 0);
    }

    [Theory]
    [InlineData("Support", "staff")]
    [InlineData("admin", "customer")]
    [InlineData("Admin", "staff")]
    public async Task T24_non_admin_is_not_allowed(string role, string userType)
    {
        await using var app = await StartAsync();
        var ids = await SeedAsync(app, 3);
        var h = () => app.Headers(role, userType);
        var id = ids[0].ToString();
        var calls = new Func<Task>[]
        {
            () => app.Client.ListDeadLetterQueuesAsync(new ListDeadLetterQueuesRequest { Service = app.Svc }, h()).ResponseAsync,
            () => app.Client.PeekDeadLettersAsync(new PeekDeadLettersRequest { Service = app.Svc, Queue = app.Dlq }, h()).ResponseAsync,
            () => app.Client.ReplayDeadLetterAsync(new DeadLetterRequest { Service = app.Svc, Queue = app.Dlq, MessageId = id }, h()).ResponseAsync,
            () => app.Client.ReplayAllDeadLettersAsync(new ReplayAllDeadLettersRequest { Service = app.Svc, Queue = app.Dlq }, h()).ResponseAsync,
            () => app.Client.DiscardDeadLetterAsync(new DeadLetterRequest { Service = app.Svc, Queue = app.Dlq, MessageId = id }, h()).ResponseAsync,
        };

        foreach (var call in calls) await AssertErrorAsync(StatusCode.PermissionDenied, "ROLE_NOT_ALLOWED", call);

        Assert.Equal(3u, await app.Rabbit.MessageCountAsync(app.Dlq));
        Assert.Empty(await AuditAsync(app));
    }

    [Fact]
    public async Task Role_Admin_capitalized_is_rejected()
    {
        await using var app = await StartAsync();

        await AssertErrorAsync(StatusCode.PermissionDenied, "ROLE_NOT_ALLOWED",
            () => app.Client.ListDeadLetterQueuesAsync(new ListDeadLetterQueuesRequest { Service = app.Svc }, app.Headers("Admin")).ResponseAsync);
    }

    [Fact]
    public async Task Role_admin_lowercase_is_allowed()
    {
        await using var app = await StartAsync();

        var response = await app.Client.ListDeadLetterQueuesAsync(new ListDeadLetterQueuesRequest { Service = app.Svc }, app.Headers("admin"));

        Assert.Single(response.Queues);
    }

    /// <summary>Advisory locks held in this test's database, seen from a separate connection.</summary>
    private static async Task<long> AdvisoryLockCountAsync(App a)
    {
        await using var conn = new NpgsqlConnection(a.Cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())", conn);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Lock_is_released_when_pass_ends()
    {
        await using var app = await StartAsync();
        await SeedAsync(app, 2);

        Assert.Equal(2u, (await app.Client.ReplayAllDeadLettersAsync(new ReplayAllDeadLettersRequest { Service = app.Svc, Queue = app.Dlq }, app.Headers())).ReplayedCount);

        Assert.Equal(0, await AdvisoryLockCountAsync(app));
    }

    [Fact]
    public async Task Lock_is_released_when_pass_throws()
    {
        await using var app = await StartAsync(configure: s => s.AddSingleton<DeadLetterPass>(sp => new ThrowOncePass(sp.GetRequiredService<RabbitMqConnection>())));
        await SeedAsync(app, 2);
        var request = new ReplayAllDeadLettersRequest { Service = app.Svc, Queue = app.Dlq };

        await Assert.ThrowsAsync<RpcException>(() => app.Client.ReplayAllDeadLettersAsync(request, app.Headers()).ResponseAsync);

        Assert.Equal(0, await AdvisoryLockCountAsync(app));
        Assert.Equal(2u, (await app.Client.ReplayAllDeadLettersAsync(request, app.Headers())).ReplayedCount); // not DLQ_BUSY
    }

    [Fact]
    public async Task Unknown_service_or_queue_is_dlq_not_found()
    {
        await using var app = await StartAsync();
        await SeedAsync(app, 1);
        var other = new DeadLetterRequest { Service = "other", Queue = app.Dlq, MessageId = Guid.NewGuid().ToString() };
        var unknownQueue = new DeadLetterRequest { Service = app.Svc, Queue = $"{app.Svc}.x.dlq", MessageId = Guid.NewGuid().ToString() };

        await AssertErrorAsync(StatusCode.NotFound, "DLQ_NOT_FOUND", () => app.Client.ListDeadLetterQueuesAsync(new ListDeadLetterQueuesRequest { Service = "other" }, app.Headers()).ResponseAsync);
        await AssertErrorAsync(StatusCode.NotFound, "DLQ_NOT_FOUND", () => app.Client.ReplayDeadLetterAsync(other, app.Headers()).ResponseAsync);
        await AssertErrorAsync(StatusCode.NotFound, "DLQ_NOT_FOUND", () => app.Client.DiscardDeadLetterAsync(unknownQueue, app.Headers()).ResponseAsync);
        await AssertErrorAsync(StatusCode.NotFound, "DLQ_NOT_FOUND", () => app.Client.PeekDeadLettersAsync(new PeekDeadLettersRequest { Service = app.Svc, Queue = unknownQueue.Queue }, app.Headers()).ResponseAsync);
        await AssertErrorAsync(StatusCode.NotFound, "DLQ_NOT_FOUND", () => app.Client.ReplayAllDeadLettersAsync(new ReplayAllDeadLettersRequest { Service = app.Svc, Queue = unknownQueue.Queue }, app.Headers()).ResponseAsync);
        Assert.Equal(1u, await app.Rabbit.MessageCountAsync(app.Dlq));
    }

    [Fact]
    public async Task Missing_message_id_is_dlq_message_not_found()
    {
        await using var app = await StartAsync();
        var ids = (await SeedAsync(app, 3)).Select(i => i.ToString()).ToList();
        var request = new DeadLetterRequest { Service = app.Svc, Queue = app.Dlq, MessageId = Guid.NewGuid().ToString() };

        await AssertErrorAsync(StatusCode.NotFound, "DLQ_MESSAGE_NOT_FOUND", () => app.Client.ReplayDeadLetterAsync(request, app.Headers()).ResponseAsync);
        await AssertErrorAsync(StatusCode.NotFound, "DLQ_MESSAGE_NOT_FOUND", () => app.Client.DiscardDeadLetterAsync(request, app.Headers()).ResponseAsync);

        Assert.Equal(ids, (await PeekAsync(app)).Messages.Select(m => m.MessageId));
        Assert.Empty(await AuditAsync(app));
    }

    [Fact]
    public async Task Empty_dlq_returns_promptly()
    {
        await using var app = await StartAsync();
        var clock = Stopwatch.StartNew();

        await AssertErrorAsync(StatusCode.NotFound, "DLQ_MESSAGE_NOT_FOUND",
            () => app.Client.ReplayDeadLetterAsync(new DeadLetterRequest { Service = app.Svc, Queue = app.Dlq, MessageId = Guid.NewGuid().ToString() }, app.Headers()).ResponseAsync);
        Assert.Equal(0u, (await app.Client.ReplayAllDeadLettersAsync(new ReplayAllDeadLettersRequest { Service = app.Svc, Queue = app.Dlq }, app.Headers())).ReplayedCount);
        Assert.Empty((await PeekAsync(app)).Messages);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Late_arrivals_are_untouched()
    {
        await using var app = await StartAsync();
        var seeded = await SeedAsync(app, 3);
        var pass = app.Host.Services.GetRequiredService<DeadLetterPass>();
        var decided = 0;
        var audited = new List<PassAction>();

        await pass.RunAsync(app.Dlq,
            _ =>
            {
                if (decided++ == 0) SeedAsync(app, 1).GetAwaiter().GetResult(); // a 4th message arrives mid-pass
                return PassAction.Replay;
            },
            (_, action) =>
            {
                audited.Add(action);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, audited.Count);
        Assert.Equal(seeded, await DrainAsync(app, app.Queue));
        await UntilCountAsync(app, app.Dlq, 1);
    }

    /// <summary>Runs a 3-message pass that finds the DLQ emptied after the first message (count over-stated), then calls <paramref name="whileWaiting"/>.</summary>
    private static async Task<(Exception Error, TimeSpan Elapsed)> OverstatedPassAsync(App app, TimeSpan deliveryTimeout, Func<Task>? whileWaiting = null)
    {
        var connection = app.Host.Services.GetRequiredService<RabbitMqConnection>();
        var pass = new DeadLetterPass(connection) { DeliveryTimeout = deliveryTimeout };
        var clock = Stopwatch.StartNew();
        var decided = 0;

        var error = await Record.ExceptionAsync(() => pass.RunAsync(app.Dlq,
            delivery =>
            {
                if (decided++ == 0)
                {
                    DrainAsync(app, app.Dlq).GetAwaiter().GetResult(); // the other two vanish after the snapshot
                    if (whileWaiting is not null) Task.Run(whileWaiting);
                }
                return PassAction.Replay;
            },
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken));

        return (error!, clock.Elapsed);
    }

    [Fact]
    public async Task Overstated_count_ends_after_the_delivery_timeout()
    {
        await using var app = await StartAsync();
        var ids = await SeedAsync(app, 3);

        var (error, elapsed) = await OverstatedPassAsync(app, TimeSpan.FromSeconds(1));

        Assert.IsType<TimeoutException>(error);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(8));
        Assert.Equal(ids.Take(1), await DrainAsync(app, app.Queue)); // the first message was replayed and ACKed, nothing was requeued
        await UntilCountAsync(app, app.Dlq, 0);
    }

    [Fact]
    public async Task Channel_closing_mid_pass_ends_the_pass_promptly()
    {
        await using var app = await StartAsync();
        await SeedAsync(app, 3);
        var connection = app.Host.Services.GetRequiredService<RabbitMqConnection>();

        var (error, elapsed) = await OverstatedPassAsync(app, TimeSpan.FromSeconds(60), async () =>
        {
            await Task.Delay(300);
            await (await connection.GetAsync(CancellationToken.None)).CloseAsync();
        });

        Assert.IsAssignableFrom<Exception>(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"took {elapsed}");
    }

    [Fact]
    public async Task List_returns_dlqs_with_counts()
    {
        await using var app = await StartAsync();
        await SeedAsync(app, 2);

        var response = await app.Client.ListDeadLetterQueuesAsync(new ListDeadLetterQueuesRequest { Service = app.Svc }, app.Headers());

        var queue = Assert.Single(response.Queues);
        Assert.Equal(app.Dlq, queue.Name);
        Assert.Equal(2u, queue.MessageCount);
    }

    [Fact]
    public async Task Http_route_is_transcoded()
    {
        await using var app = await StartAsync();
        await SeedAsync(app, 1);
        using var http = app.Host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/dlq/{app.Svc}/queues");
        request.Headers.Add("x-bloom-user-type", "staff");
        request.Headers.Add("x-bloom-role", "admin");
        request.Headers.Add("x-bloom-user-id", app.Actor.ToString());
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(app.Dlq, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
