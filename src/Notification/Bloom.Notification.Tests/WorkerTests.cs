using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Identity.Contracts;
using Bloom.Notification.Data;
using Bloom.Notification.Sockets;
using Bloom.Notification.Worker;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace Bloom.Notification.Tests;

/// <summary>Worker mode through the real broker and the cross-pod paths (spec §8; plan N11): two API hosts and one worker host on one database, the run's Redis and broker.</summary>
public class WorkerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Hosts(TestNotification Api1, TestNotification Api2, TestNotification Worker) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Worker.DisposeAsync();
            await Api2.DisposeAsync();
            await Api1.DisposeAsync();
        }
    }

    private async Task<Hosts> StartAsync()
    {
        var api1 = await TestNotification.StartAsync(containers);
        var api2 = await TestNotification.StartAsync(containers, o => o.ConnectionString = api1.ConnectionString);
        var worker = await TestNotification.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api1.ConnectionString; });
        return new Hosts(api1, api2, worker);
    }

    private async Task<IChannel> ChannelAsync()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync(Ct);
        return await connection.CreateChannelAsync(cancellationToken: Ct);
    }

    private static Task PublishAsync(IChannel channel, string producer, string messageType, object payload, Guid? messageId = null)
    {
        var e = new Envelope(messageId ?? Guid.NewGuid(), messageType, MessageKinds.Event, producer, DateTime.UtcNow, Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, Web));
        return channel.BasicPublishAsync($"{producer}.events", messageType, false,
            new BasicProperties { MessageId = e.MessageId.ToString(), Type = messageType, DeliveryMode = DeliveryModes.Persistent }, e.ToUtf8Bytes()).AsTask();
    }

    private static string Producer(string messageType) => Consumers.EventConsumer.Handlers.Single(h => h.Binding.MessageType == messageType).Binding.Producer!;

    private static async Task<Guid> CustomerAsync(TestNotification t)
    {
        var account = Guid.NewGuid();
        await Seed.ContactAsync(t, account, $"{account:N}@bloom.test", "Ada");
        return account;
    }

    [Fact]
    public async Task Every_queue_is_bound()
    {
        await using var hosts = await StartAsync();

        var bindings = (await containers.RabbitCtlAsync("list_bindings", "-q"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();
        var queues = (await containers.RabbitCtlAsync("list_queues", "name", "-q")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToHashSet();

        Assert.Equal(17, Consumers.EventConsumer.Handlers.Count);
        foreach (var (handler, binding) in Consumers.EventConsumer.Handlers)
        {
            var queue = $"notification.{handler}";
            Assert.Contains(bindings, b => b.Length >= 5 && b[0] == $"{binding.Producer}.events" && b[2] == queue && b[4] == binding.MessageType);
            Assert.Contains(queue, queues);
            Assert.Contains(MessageNames.DeadLetterQueue(queue), queues);
            foreach (var (delay, _) in MessageNames.RetryDelays) Assert.Contains(MessageNames.RetryQueue(queue, delay), queues);
        }
    }

    [Fact]
    public async Task N01_order_paid_published_twice_makes_one_row()
    {
        await using var hosts = await StartAsync();
        await using var channel = await ChannelAsync();
        var account = await CustomerAsync(hosts.Api1);
        var sentinel = await CustomerAsync(hosts.Api1);
        var id = Guid.NewGuid();
        var paid = ConsumerTests.Payload(OrderMessages.Paid, account);

        await PublishAsync(channel, "order", OrderMessages.Paid, paid, id);
        await PublishAsync(channel, "order", OrderMessages.Paid, paid, id);
        await PublishAsync(channel, "order", OrderMessages.Paid, ConsumerTests.Payload(OrderMessages.Paid, sentinel)); // queued after the duplicate: its row proves the duplicate was handled
        await Poll.UntilAsync(async () => (await Seed.RowsAsync(hosts.Api1, sentinel)).Count == 1, 15, "the sentinel row");

        Assert.Single(await Seed.RowsAsync(hosts.Api1, account));
        await using var db = hosts.Api1.NewDb();
        Assert.Equal(1, await db.EmailJobs.CountAsync(j => j.AccountId == account, Ct));
    }

    [Fact]
    public async Task N02_shipped_before_order_ref_is_retried_then_processed()
    {
        await using var hosts = await StartAsync();
        await using var channel = await ChannelAsync();
        var account = await CustomerAsync(hosts.Api1);

        await PublishAsync(channel, "fulfillment", FulfillmentMessages.ShipmentShipped, ConsumerTests.Payload(FulfillmentMessages.ShipmentShipped, account));
        await Task.Delay(1500, Ct);
        Assert.Empty(await Seed.RowsAsync(hosts.Api1, account));
        await PublishAsync(channel, "order", OrderMessages.Paid, ConsumerTests.Payload(OrderMessages.Paid, account));

        await Poll.UntilAsync(async () => (await Seed.RowsAsync(hosts.Api1, account)).Any(r => r.Type == "order.shipped"), 15, "the order.shipped row after the retry");
    }

    [Fact]
    public async Task Every_notifying_event_flows_through_the_broker()
    {
        await using var hosts = await StartAsync();
        await using var channel = await ChannelAsync();
        var account = await CustomerAsync(hosts.Api1);
        await Seed.OrderRefAsync(hosts.Api1, ConsumerTests.Order, account, ConsumerTests.Number);
        var sent = new List<(Guid Id, string Type)>();

        foreach (var row in ConsumerTests.NotifyingMessages)
        {
            var (messageType, type, _) = row.Data;
            var id = Guid.NewGuid();
            await PublishAsync(channel, Producer(messageType), messageType, ConsumerTests.Payload(messageType, account), id);
            sent.Add((id, type));
        }

        await Poll.UntilAsync(async () =>
        {
            await using var db = hosts.Api1.NewDb();
            return await db.EmailJobs.CountAsync(j => sent.Select(s => s.Id).Contains(j.SourceMessageId), Ct) == sent.Count;
        }, 15, "one email job per event");
        await using var check = hosts.Api1.NewDb();
        var jobs = await check.EmailJobs.AsNoTracking().ToListAsync(Ct);
        var rows = await check.Notifications.AsNoTracking().ToListAsync(Ct);
        foreach (var (id, type) in sent)
        {
            Assert.Single(jobs, j => j.SourceMessageId == id && j.Template == type);
            Assert.Equal(Templates.NotificationTypes.InApp.Contains(type) ? 1 : 0, rows.Count(r => r.SourceMessageId == id && r.Type == type));
        }
    }

    [Fact]
    public async Task N07_push_from_the_worker_reaches_either_host()
    {
        await using var hosts = await StartAsync();
        await using var channel = await ChannelAsync();
        var a = await CustomerAsync(hosts.Api1);
        var b = await CustomerAsync(hosts.Api1);
        await using var ca = await Sockets.ConnectAsync(hosts.Api1, await Sockets.TicketAsync(hosts.Api1, a));
        await using var cb = await Sockets.ConnectAsync(hosts.Api2, await Sockets.TicketAsync(hosts.Api2, b));
        var inboxA = Sockets.Inbox(ca);
        var inboxB = Sockets.Inbox(cb);

        await PublishAsync(channel, "order", OrderMessages.Paid, ConsumerTests.Payload(OrderMessages.Paid, a));
        await PublishAsync(channel, "order", OrderMessages.Paid, ConsumerTests.Payload(OrderMessages.Paid, b));

        var pushA = await Sockets.NextAsync(inboxA, 5);
        var pushB = await Sockets.NextAsync(inboxB, 5);
        Assert.Equal("order.confirmed", pushA.Type);
        Assert.Equal(Assert.Single(await Seed.RowsAsync(hosts.Api1, a)).Id, pushA.Id);
        Assert.Equal(Assert.Single(await Seed.RowsAsync(hosts.Api1, b)).Id, pushB.Id);
        Assert.False(inboxA.Reader.TryRead(out _));
        Assert.False(inboxB.Reader.TryRead(out _));
    }

    [Fact]
    public async Task N06_tokens_revoked_closes_sockets_on_every_pod()
    {
        await using var hosts = await StartAsync();
        await using var channel = await ChannelAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using var a1 = await Sockets.ConnectAsync(hosts.Api1, await Sockets.TicketAsync(hosts.Api1, a));
        await using var a2 = await Sockets.ConnectAsync(hosts.Api2, await Sockets.TicketAsync(hosts.Api2, a));
        await using var b1 = await Sockets.ConnectAsync(hosts.Api1, await Sockets.TicketAsync(hosts.Api1, b));
        var consumed = new TaskCompletionSource();
        var subscriber = hosts.Api1.Services.GetRequiredService<IConnectionMultiplexer>().GetSubscriber();
        await subscriber.SubscribeAsync(RedisChannel.Literal(Disconnects.Channel), (_, m) => { if (m == a.ToString("D")) consumed.TrySetResult(); });

        await PublishAsync(channel, "identity", IdentityMessages.TokensRevoked, new TokensRevokedV1(a, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await consumed.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        await Sockets.ClosedAsync(a1, TimeSpan.FromSeconds(2));
        await Sockets.ClosedAsync(a2, TimeSpan.FromSeconds(2));
        Assert.Equal(HubConnectionState.Connected, b1.State);
    }

    [Fact]
    public async Task Email_is_sent_by_the_worker()
    {
        await using var hosts = await StartAsync();
        await using var channel = await ChannelAsync();
        var account = await CustomerAsync(hosts.Api1);

        await PublishAsync(channel, "order", OrderMessages.Paid, ConsumerTests.Payload(OrderMessages.Paid, account));

        await Poll.UntilAsync(async () =>
        {
            hosts.Worker.Clock.Advance(TimeSpan.FromSeconds(2)); // ticks the sender's PeriodicTimer (plan N3)
            await using var db = hosts.Api1.NewDb();
            return await db.EmailJobs.AnyAsync(j => j.AccountId == account && j.Status == EmailStatuses.Sent, Ct);
        }, 15, "the job to be sent by the worker");
        Assert.Single(hosts.Worker.Resend.Delivered);
        Assert.Empty(hosts.Api1.Resend.Requests);
        Assert.Empty(hosts.Api2.Resend.Requests);
    }

    [Fact]
    public async Task Dlq_admin_is_hosted()
    {
        await using var hosts = await StartAsync();
        using var admin = hosts.Api2.StaffClient("Admin");

        var response = await admin.GetAsync("/api/admin/dlq/notification/queues", Ct);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var queues = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("queues").EnumerateArray()
            .Select(q => q.GetProperty("name").GetString()!).ToList();
        Assert.Equal(17, queues.Count(q => q.StartsWith("notification.", StringComparison.Ordinal) && q.EndsWith(".dlq", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Worker_runs_sender_and_purge()
    {
        await using var hosts = await StartAsync();

        static HashSet<Type> Hosted(TestNotification t) => t.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToHashSet();

        Assert.Contains(typeof(EmailSender), Hosted(hosts.Worker));
        Assert.Contains(typeof(DailyPurge), Hosted(hosts.Worker));
        Assert.DoesNotContain(typeof(DisconnectListener), Hosted(hosts.Worker));
        foreach (var api in new[] { hosts.Api1, hosts.Api2 })
        {
            Assert.Contains(typeof(DisconnectListener), Hosted(api));
            Assert.DoesNotContain(typeof(EmailSender), Hosted(api));
            Assert.DoesNotContain(typeof(DailyPurge), Hosted(api));
        }
    }
}
