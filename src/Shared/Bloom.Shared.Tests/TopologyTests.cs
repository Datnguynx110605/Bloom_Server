using System.Text;
using System.Text.Json;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Shared.Tests;

public class TopologyTests(Containers containers)
{
    private sealed class NoopHandler : IMessageHandler
    {
        public Task HandleAsync(Envelope envelope, CancellationToken ct) => Task.CompletedTask;
    }

    private static TopologyInitializer Initializer(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<TopologyInitializer>().Single();

    /// <summary>rabbitmqctl prints arguments as <c>[[key, type, value], ...]</c>.</summary>
    private static Dictionary<string, JsonElement> Arguments(JsonElement queue) =>
        queue.GetProperty("arguments").EnumerateArray().ToDictionary(a => a[0].GetString()!, a => a[2]);

    [Fact]
    public void Retry_queue_arguments_match_section_6()
    {
        Assert.Equal(new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-message-ttl"] = 30000,
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = "s.h",
        }, TopologyInitializer.RetryQueueArguments("s.h", 30000));
    }

    [Fact]
    public void Consumer_queue_arguments_match_section_6()
    {
        Assert.Equal(new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = "s.h.dlq",
        }, TopologyInitializer.ConsumerQueueArguments("s.h"));
    }

    [Fact]
    public async Task T07_topology_declares_retry_queues_with_ttl_and_dlq()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers,
            b => b.AddConsumer<NoopHandler>("h", ConsumerBinding.Event("order", "order.paid.v1")).AddWorker());
        var service = host.Services.GetRequiredService<BloomOptions>().ServiceName;
        var queue = MessageNames.ConsumerQueue(service, "h");

        await Initializer(host).StartAsync(ct);

        var json = await containers.RabbitCtlAsync("list_queues", "name", "type", "arguments", "--formatter", "json");
        var mine = JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Where(q => q.GetProperty("name").GetString()!.StartsWith(queue)).ToDictionary(q => q.GetProperty("name").GetString()!);

        var expectedNames = new[] { queue, MessageNames.DeadLetterQueue(queue) }
            .Concat(MessageNames.RetryDelays.Select(d => MessageNames.RetryQueue(queue, d.Delay)));
        Assert.Equal(expectedNames.Order(), mine.Keys.Order());
        Assert.All(mine.Values, q => Assert.Equal("quorum", q.GetProperty("type").GetString()));

        foreach (var (delay, ms) in MessageNames.RetryDelays)
        {
            var args = Arguments(mine[MessageNames.RetryQueue(queue, delay)]);
            Assert.Equal(ms, args["x-message-ttl"].GetInt32());
            Assert.Equal("", args["x-dead-letter-exchange"].GetString());
            Assert.Equal(queue, args["x-dead-letter-routing-key"].GetString());
        }
        var consumerArgs = Arguments(mine[queue]);
        Assert.Equal("", consumerArgs["x-dead-letter-exchange"].GetString());
        Assert.Equal(MessageNames.DeadLetterQueue(queue), consumerArgs["x-dead-letter-routing-key"].GetString());
        Assert.DoesNotContain("x-message-ttl", Arguments(mine[MessageNames.DeadLetterQueue(queue)]).Keys);
    }

    [Fact]
    public async Task Event_binding_routes_to_consumer_queue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers,
            b => b.AddConsumer<NoopHandler>("h", ConsumerBinding.Event("order", "order.paid.v1")).AddWorker());
        var queue = MessageNames.ConsumerQueue(host.Services.GetRequiredService<BloomOptions>().ServiceName, "h");
        await Initializer(host).StartAsync(ct);

        await using var rabbit = await Rabbit.ConnectAsync(containers);
        await rabbit.PublishAsync("order.events", "order.other.v1", "other"u8.ToArray());
        await rabbit.PublishAsync("order.events", "order.paid.v1", "paid"u8.ToArray());

        var got = await rabbit.BasicGetAsync(queue);
        Assert.Equal("paid", Encoding.UTF8.GetString(got!.Body.Span));
        Assert.Null(await rabbit.BasicGetAsync(queue));
    }

    [Fact]
    public async Task Command_binding_and_targets_are_declared()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers,
            b => b.AddConsumer<NoopHandler>("cmd", ConsumerBinding.Command("s.do-thing.v1")).SendsCommandsTo("payment").AddWorker());
        var service = host.Services.GetRequiredService<BloomOptions>().ServiceName;
        await Initializer(host).StartAsync(ct);

        await using var rabbit = await Rabbit.ConnectAsync(containers);
        await rabbit.Channel.ExchangeDeclarePassiveAsync(MessageNames.CommandExchange(service), ct);
        await rabbit.Channel.ExchangeDeclarePassiveAsync(MessageNames.CommandExchange("payment"), ct);
        await rabbit.Channel.ExchangeDeclarePassiveAsync(MessageNames.EventExchange(service), ct);

        await rabbit.PublishAsync(MessageNames.CommandExchange(service), "s.do-thing.v1", "go"u8.ToArray());
        var got = await rabbit.BasicGetAsync(MessageNames.ConsumerQueue(service, "cmd"));
        Assert.Equal("go", Encoding.UTF8.GetString(got!.Body.Span));
    }

    [Fact]
    public async Task Declarations_are_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers,
            b => b.AddConsumer<NoopHandler>("h", ConsumerBinding.Event("order", "order.paid.v1")).AddWorker());
        var initializer = Initializer(host);

        await initializer.StartAsync(ct);
        await initializer.StartAsync(ct);
    }

    /// <summary>
    /// Declares one consumer queue, caps its delivery limit at 1 with a policy (the 4.1 default is 20), publishes one message and
    /// NACK-requeues it past the limit. Returns once the broker has moved it to the DLQ.
    /// </summary>
    private async Task<(IHost Host, string Queue, Guid Id)> DeadLetteredByBrokerAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = await TestApp.BuildAsync(containers,
            b => b.AddConsumer<NoopHandler>("h", ConsumerBinding.Event("order", "order.paid.v1")).AddWorker());
        var queue = MessageNames.ConsumerQueue(host.Services.GetRequiredService<BloomOptions>().ServiceName, "h");
        await Initializer(host).StartAsync(ct);
        await containers.RabbitCtlAsync("set_policy", $"limit-{queue}", $"^{queue}$", """{"delivery-limit":1}""", "--apply-to", "quorum_queues");

        await using var rabbit = await Rabbit.ConnectAsync(containers);
        var id = Guid.CreateVersion7();
        await rabbit.Channel.BasicPublishAsync("", queue, false,
            new BasicProperties { MessageId = id.ToString(), Type = "order.paid.v1", DeliveryMode = DeliveryModes.Persistent }, "paid"u8.ToArray(), ct);
        await Poll.UntilAsync(async () =>
        {
            if (await rabbit.Channel.BasicGetAsync(queue, autoAck: false, ct) is { } got)
                await rabbit.Channel.BasicNackAsync(got.DeliveryTag, false, requeue: true, ct);
            return await rabbit.MessageCountAsync(MessageNames.DeadLetterQueue(queue)) == 1;
        }, 30, "the broker to dead-letter the message");
        return (host, queue, id);
    }

    [Fact]
    public async Task Message_past_delivery_limit_lands_in_dlq()
    {
        var (host, queue, id) = await DeadLetteredByBrokerAsync();
        using var _ = host;
        await using var rabbit = await Rabbit.ConnectAsync(containers);

        var dlq = Assert.IsType<BasicGetResult>(await rabbit.BasicGetAsync(MessageNames.DeadLetterQueue(queue)));
        Assert.Equal(id.ToString(), dlq.BasicProperties.MessageId);
        Assert.Equal("paid", Encoding.UTF8.GetString(dlq.Body.Span));
        Assert.Null(await rabbit.BasicGetAsync(MessageNames.DeadLetterQueue(queue)));
        Assert.Null(await rabbit.BasicGetAsync(queue));
    }

    [Fact]
    public async Task Broker_dead_lettered_message_is_listed_and_replayable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (host, queue, id) = await DeadLetteredByBrokerAsync();
        using var _ = host;
        using var scope = host.Services.CreateScope();
        var admin = ActivatorUtilities.CreateInstance<DeadLetterAdminService>(scope.ServiceProvider);
        var service = host.Services.GetRequiredService<BloomOptions>().ServiceName;
        var dlq = MessageNames.DeadLetterQueue(queue);
        FakeServerCallContext Staff() => new("m", new Metadata
        {
            { "x-bloom-user-type", "staff" }, { "x-bloom-role", "admin" }, { "x-bloom-user-id", Guid.NewGuid().ToString() },
        }, ct);

        var listed = (await admin.PeekDeadLetters(new PeekDeadLettersRequest { Service = service, Queue = dlq }, Staff())).Messages.Single();
        Assert.Equal(id.ToString(), listed.MessageId);
        Assert.Equal("", listed.FailureReason);
        Assert.Equal("", listed.FailedAt);

        await admin.ReplayDeadLetter(new DeadLetterRequest { Service = service, Queue = dlq, MessageId = id.ToString() }, Staff());

        await using var rabbit = await Rabbit.ConnectAsync(containers);
        await Poll.UntilAsync(async () => await rabbit.MessageCountAsync(dlq) == 0, 10, "the DLQ to empty");
        var replayed = Assert.IsType<BasicGetResult>(await rabbit.BasicGetAsync(queue));
        Assert.Equal(id.ToString(), replayed.BasicProperties.MessageId);
    }
}
