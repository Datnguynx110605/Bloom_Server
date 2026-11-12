using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Shared.Messaging;

/// <summary>Declares exchanges, queues and bindings at worker startup (§6). Every declaration is idempotent.</summary>
internal sealed class TopologyInitializer(RabbitMqConnection connection, ConsumerCatalog catalog, BloomOptions options)
    : IHostedService
{
    private static readonly Dictionary<string, object?> Quorum = new() { ["x-queue-type"] = "quorum" };

    /// <summary>Quorum queues drop a message at the delivery limit unless a DLX is set (LLD-ADR-015).</summary>
    internal static IDictionary<string, object?> ConsumerQueueArguments(string consumerQueue) =>
        new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = MessageNames.DeadLetterQueue(consumerQueue),
        };

    internal static IDictionary<string, object?> RetryQueueArguments(string consumerQueue, int ttlMs) =>
        new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-message-ttl"] = ttlMs,
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = consumerQueue,
        };

    public async Task StartAsync(CancellationToken ct)
    {
        await using var channel = await (await connection.GetAsync(ct)).CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(MessageNames.EventExchange(options.ServiceName), ExchangeType.Topic, durable: true, cancellationToken: ct);
        if (catalog.Consumers.Any(c => c.Bindings.Any(b => b.Kind == MessageKinds.Command)))
            await channel.ExchangeDeclareAsync(MessageNames.CommandExchange(options.ServiceName), ExchangeType.Direct, durable: true, cancellationToken: ct);
        foreach (var producer in catalog.Consumers.SelectMany(c => c.Bindings).Where(b => b.Kind == MessageKinds.Event).Select(b => b.Producer!).Distinct())
            await channel.ExchangeDeclareAsync(MessageNames.EventExchange(producer), ExchangeType.Topic, durable: true, cancellationToken: ct);
        foreach (var target in catalog.CommandTargets)
            await channel.ExchangeDeclareAsync(MessageNames.CommandExchange(target), ExchangeType.Direct, durable: true, cancellationToken: ct);

        foreach (var consumer in catalog.Consumers)
        {
            await DeclareQueueAsync(channel, consumer.Queue, ConsumerQueueArguments(consumer.Queue), ct);
            foreach (var binding in consumer.Bindings)
                await channel.QueueBindAsync(consumer.Queue, binding.Exchange(options.ServiceName), binding.MessageType, cancellationToken: ct);
            foreach (var (delay, ms) in MessageNames.RetryDelays)
                await DeclareQueueAsync(channel, MessageNames.RetryQueue(consumer.Queue, delay), RetryQueueArguments(consumer.Queue, ms), ct);
            await DeclareQueueAsync(channel, MessageNames.DeadLetterQueue(consumer.Queue), Quorum, ct);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private static Task DeclareQueueAsync(IChannel channel, string queue, IDictionary<string, object?> arguments, CancellationToken ct) =>
        channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments, cancellationToken: ct);
}
