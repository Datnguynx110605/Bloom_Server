using System.Net;
using System.Text;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Launch.Worker;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Launch.Tests;

/// <summary>Worker mode through the real broker (spec §8; plan R18): an API host and a worker host on one database.</summary>
public class WorkerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestLaunch Api, TestLaunch Worker)> StartAsync()
    {
        var api = await TestLaunch.StartAsync(containers);
        var worker = await TestLaunch.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api.ConnectionString; });
        return (api, worker);
    }

    private async Task<(IConnection Connection, IChannel Channel)> RabbitAsync()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync(Ct);
        return (connection, await connection.CreateChannelAsync(cancellationToken: Ct));
    }

    private static Envelope NewEnvelope(string messageType, string kind, string producer, object payload, Guid? messageId = null) =>
        new(messageId ?? Guid.CreateVersion7(), messageType, kind, producer, DateTime.UtcNow, Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web));

    private static Task PublishAsync(IChannel channel, string exchange, Envelope e) =>
        channel.BasicPublishAsync(exchange, e.MessageType, false,
            new BasicProperties { MessageId = e.MessageId.ToString(), Type = e.MessageType, DeliveryMode = DeliveryModes.Persistent }, e.ToUtf8Bytes()).AsTask();

    private static async Task<string> QueueOnAsync(IChannel channel, string exchange, string key)
    {
        var queue = $"test-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(queue, exchange, key, cancellationToken: Ct);
        return queue;
    }

    private static async Task<Envelope> NextAsync(IChannel channel, string queue, string what)
    {
        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync(queue, true, Ct)) is not null, 20, what);
        return Envelope.Parse(got!.Body.Span);
    }

    [Fact]
    public async Task Every_queue_is_bound()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var bindings = (await containers.RabbitCtlAsync("list_bindings", "-q"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();
        var queues = (await containers.RabbitCtlAsync("list_queues", "name", "-q")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToHashSet();

        (string Exchange, string Queue, string Key)[] expected =
        [
            ("order.events", "launch.order-paid", OrderMessages.Paid),
            ("launch.commands", "launch.release-allocation-hold", LaunchMessages.ReleaseAllocationHold),
        ];
        foreach (var (exchange, queue, key) in expected)
        {
            Assert.Contains(bindings, b => b.Length >= 5 && b[0] == exchange && b[2] == queue && b[4] == key);
            Assert.Contains(queue, queues);
            Assert.Contains(MessageNames.DeadLetterQueue(queue), queues);
            foreach (var (delay, _) in MessageNames.RetryDelays) Assert.Contains(MessageNames.RetryQueue(queue, delay), queues);
        }
    }

    [Fact]
    public async Task Paid_and_release_through_the_broker()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var released = await QueueOnAsync(channel, "launch.events", LaunchMessages.HoldReleased);
        var (launch, _, paidOrder) = await Holds.HeldAsync(api);
        var (_, _, sentinelOrder) = await Holds.HeldAsync(api, on: launch);
        var (_, _, releasedOrder) = await Holds.HeldAsync(api, on: launch);
        var paid = NewEnvelope(OrderMessages.Paid, MessageKinds.Event, "order", Holds.Paid(paidOrder, launch.Id));

        await PublishAsync(channel, "order.events", paid);
        await PublishAsync(channel, "order.events", paid); // same messageId: the inbox drops it
        await PublishAsync(channel, "order.events", NewEnvelope(OrderMessages.Paid, MessageKinds.Event, "order", Holds.Paid(sentinelOrder, launch.Id))); // queued after the duplicate

        await Poll.UntilAsync(async () => (await Holds.HoldAsync(api, sentinelOrder)).Status == HoldStatuses.Sold, 15, "sentinel hold sold");
        var hold = await Holds.HoldAsync(api, paidOrder);
        Assert.Equal(HoldStatuses.Sold, hold.Status);
        Assert.Equal(1, hold.Version);
        Assert.Equal((2, 4), await Holds.SkuAsync(api, launch.Skus[0])); // 3 holds of 2: two sold once each, one still held

        await PublishAsync(channel, "launch.commands", NewEnvelope(LaunchMessages.ReleaseAllocationHold, MessageKinds.Command, "order", new ReleaseAllocationHoldV1(releasedOrder, "checkout_expired")));

        await Poll.UntilAsync(async () => (await Holds.HoldAsync(api, releasedOrder)).Status == HoldStatuses.Released, 15, "hold released");
        Assert.Equal(releasedOrder, (await NextAsync(channel, released, "launch.hold-released.v1 on launch.events")).GetPayload<HoldReleasedV1>().OrderId);
        Assert.Equal((0, 4), await Holds.SkuAsync(api, launch.Skus[0]));
        await Holds.InvariantAsync(api, launch.Id);
    }

    [Fact]
    public async Task Admin_events_reach_the_broker()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var changed = await QueueOnAsync(channel, "launch.events", LaunchMessages.StageChanged);
        var audit = await QueueOnAsync(channel, "launch.events", IdentityMessages.StaffActionRecorded);
        var launch = await Seed.LaunchAsync(api, LaunchStages.Draft);

        await api.Admin.PublishLaunchAsync(new() { LaunchId = launch.Id.ToString("D"), Version = 0 }, Api.Staff(), cancellationToken: Ct).ResponseAsync; // the API host writes; the worker's relay publishes

        var stage = (await NextAsync(channel, changed, "launch.stage-changed.v1 on launch.events")).GetPayload<LaunchStageChangedV1>();
        Assert.Equal(launch.Id, stage.LaunchId);
        var action = await NextAsync(channel, audit, "identity.staff-action-recorded.v1 on launch.events");
        Assert.Equal(IdentityMessages.StaffActionRecorded, action.MessageType);
    }

    [Fact]
    public async Task Worker_admits_and_schedules_on_its_own()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var launch = await Seed.LaunchAsync(api, LaunchStages.Announced, launchIn: TimeSpan.FromSeconds(2));
        var account = Guid.NewGuid();
        var room = new WaitingRoomRequest { LaunchId = launch.Id.ToString("D") };
        Assert.Equal("queued", (await api.Customer.JoinWaitingRoomAsync(room, Api.Customer(account), cancellationToken: Ct).ResponseAsync).State);

        await Poll.UntilAsync(async () =>
        {
            await using var db = api.NewDb();
            return (await db.Launches.AsNoTracking().SingleAsync(l => l.Id == launch.Id, Ct)).Stage == LaunchStages.PreorderOpen;
        }, 15, "launch is preorder_open");
        await Poll.UntilAsync(async () => (await api.Customer.GetWaitingRoomStatusAsync(room, Api.Customer(account), cancellationToken: Ct).ResponseAsync).State == "admitted", 15, "customer admitted");
    }

    [Fact]
    public async Task Dlq_admin_is_hosted()
    {
        var (api, worker) = await StartAsync(); // the worker declares the queues
        await using var _ = api;
        await using var __ = worker;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dlq/launch/queues").WithCaller(Guid.NewGuid(), "staff", "Admin"); // the value Bloom.Shared checks today
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());
        using var response = await api.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        foreach (var handler in new[] { "order-paid", "release-allocation-hold" }) Assert.Contains($"launch.{handler}.dlq", body);
    }

    [Fact]
    public async Task Worker_runs_the_jobs_and_api_does_not()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var hosted = worker.Services.GetServices<IHostedService>().ToList();
        Assert.Single(hosted.OfType<AdmissionWorker>());
        Assert.Single(hosted.OfType<StageScheduler>());
        Assert.Single(hosted.OfType<StuckHoldMonitor>());
        var apiHosted = api.Services.GetServices<IHostedService>().ToList();
        Assert.Empty(apiHosted.OfType<AdmissionWorker>());
        Assert.Empty(apiHosted.OfType<StageScheduler>());
        Assert.Empty(apiHosted.OfType<StuckHoldMonitor>());
    }

    [Fact]
    public async Task HoldAllocation_has_no_http_route()
    {
        await using var t = await TestLaunch.StartAsync(containers);
        var account = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bloom.launch.v1.LaunchInternal/HoldAllocation")
        {
            Content = new StringContent("""{"orderId":"x","passToken":"x","lines":[]}""", Encoding.UTF8, "application/json"),
        }.WithCaller(account, "customer");

        using var response = await t.Http.SendAsync(request, Ct);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnsupportedMediaType, $"was {(int)response.StatusCode}");
    }
}
