using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bloom.Shared.Messaging;
using Bloom.Shared.Observability;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Shared.Tests;

public class OutboxRelayTests(Containers containers)
{
    private sealed record Setup(IHost Host, Rabbit Rabbit, OutboxRelay Relay, string Queue, string Exchange) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Rabbit.DisposeAsync();
            Host.Dispose();
        }
    }

    /// <summary>Host with topology started, plus queue <c>q</c> bound to the service's events exchange with <c>#</c>.</summary>
    private async Task<Setup> SetUpAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await TestApp.BuildAsync(containers, b => b.AddWorker());
        await host.Services.GetServices<IHostedService>().OfType<TopologyInitializer>().Single().StartAsync(ct);
        var exchange = MessageNames.EventExchange(host.Services.GetRequiredService<BloomOptions>().ServiceName);
        var rabbit = await Rabbit.ConnectAsync(containers);
        var queue = "q-" + exchange;
        await rabbit.Channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: true, cancellationToken: ct);
        await rabbit.Channel.QueueBindAsync(queue, exchange, "#", cancellationToken: ct);
        return new Setup(host, rabbit, host.Services.GetServices<IHostedService>().OfType<OutboxRelay>().Single(), queue, exchange);
    }

    private static async Task<Guid[]> WriteAsync(IHost host, int count, bool commit = true)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = host.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ids = Enumerable.Range(0, count).Select(i => writer.PublishEvent("t.thing-happened.v1", new { i })).ToArray();
        await db.SaveChangesAsync(ct);
        if (commit) await tx.CommitAsync(ct);
        else await tx.RollbackAsync(ct);
        return ids;
    }

    private static async Task<OutboxMessage[]> RowsAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<OutboxMessage>()
            .AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task T01_rolled_back_row_is_never_published()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await SetUpAsync();
        await WriteAsync(s.Host, 1, commit: false);

        Assert.Equal(0, await s.Relay.RunOnceAsync(ct));
        Assert.Equal(0u, await s.Rabbit.MessageCountAsync(s.Queue));
    }

    [Fact]
    public async Task T02_confirmed_row_marked_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await SetUpAsync();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Bloom.Shared",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        string? writerTrace;
        using (var activity = BloomActivity.Source.StartActivity("test"))
        {
            writerTrace = activity?.Id;
            await WriteAsync(s.Host, 1);
        }
        Assert.NotNull(writerTrace);

        Assert.Equal(1, await s.Relay.RunOnceAsync(ct));

        var row = Assert.Single(await RowsAsync(s.Host));
        Assert.Equal("sent", row.Status);
        Assert.NotNull(row.SentAt);
        Assert.Equal(0, row.Attempts);
        var got = await s.Rabbit.BasicGetAsync(s.Queue);
        Assert.NotNull(got);
        Assert.Equal(row.Payload, Encoding.UTF8.GetString(got.Body.Span));
        Assert.Equal(row.Id.ToString(), got.BasicProperties.MessageId);
        Assert.Equal(row.MessageType, got.BasicProperties.Type);
        Assert.Equal("application/json", got.BasicProperties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, got.BasicProperties.DeliveryMode);
        var traceparent = Encoding.UTF8.GetString((byte[])got.BasicProperties.Headers!["traceparent"]!);
        Assert.Equal(writerTrace.Split('-')[1], traceparent.Split('-')[1]); // same trace, relay's own span
    }

    [Fact]
    public async Task T03_nacked_row_stays_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await SetUpAsync();
        var q2 = "q2-" + s.Exchange;
        await s.Rabbit.Channel.QueueDeclareAsync(q2, durable: false, exclusive: false, autoDelete: true,
            new Dictionary<string, object?> { ["x-max-length"] = 0, ["x-overflow"] = "reject-publish" }, cancellationToken: ct);
        await s.Rabbit.Channel.QueueBindAsync(q2, s.Exchange, "#", cancellationToken: ct);
        await WriteAsync(s.Host, 1);

        Assert.Equal(1, await s.Relay.RunOnceAsync(ct));

        var row = Assert.Single(await RowsAsync(s.Host));
        Assert.Equal("pending", row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.LastError);
        Assert.Null(row.SentAt);
    }

    [Fact]
    public async Task T04_two_relays_on_200_rows_publish_each_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await SetUpAsync();
        await WriteAsync(s.Host, 200);
        var second = ActivatorUtilities.CreateInstance<OutboxRelay>(s.Host.Services);

        async Task Drain(OutboxRelay relay) { while (await relay.RunOnceAsync(ct) > 0) { } }
        await Task.WhenAll(Task.Run(() => Drain(s.Relay), ct), Task.Run(() => Drain(second), ct));

        var ids = new List<string?>();
        while (await s.Rabbit.BasicGetAsync(s.Queue) is { } m) ids.Add(m.BasicProperties.MessageId);
        Assert.Equal(200, ids.Count);
        Assert.Equal(200, ids.Distinct().Count());
        Assert.All(await RowsAsync(s.Host), r => Assert.Equal("sent", r.Status));
    }

    [Fact]
    public async Task Missing_exchange_counts_as_failure_and_relay_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await SetUpAsync();
        var a = (await WriteAsync(s.Host, 1)).Single();
        using (var scope = s.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database
                .ExecuteSqlAsync($"UPDATE outbox_messages SET exchange = 'nope.events' WHERE id = {a}", ct);

        Assert.Equal(1, await s.Relay.RunOnceAsync(ct));

        var rowA = Assert.Single(await RowsAsync(s.Host));
        Assert.Equal("pending", rowA.Status);
        Assert.Equal(1, rowA.Attempts);
        Assert.Contains("404", rowA.LastError);

        using (var scope = s.Host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database
                .ExecuteSqlAsync($"UPDATE outbox_messages SET status = 'sent', sent_at = now() WHERE id = {a}", ct);
        var b = (await WriteAsync(s.Host, 1)).Single();

        Assert.Equal(1, await s.Relay.RunOnceAsync(ct));

        Assert.Equal(b.ToString(), (await s.Rabbit.BasicGetAsync(s.Queue))!.BasicProperties.MessageId);
        Assert.Equal("sent", (await RowsAsync(s.Host)).Single(r => r.Id == b).Status);
    }

    [Fact]
    public async Task Full_batch_is_100_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await SetUpAsync();
        await WriteAsync(s.Host, 150);

        Assert.Equal(100, await s.Relay.RunOnceAsync(ct));
        Assert.Equal(50, await s.Relay.RunOnceAsync(ct));
        Assert.Equal(150u, await s.Rabbit.MessageCountAsync(s.Queue));
    }
}
