using System.Text;
using System.Text.Json;
using Bloom.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
}
