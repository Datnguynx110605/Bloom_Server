using RabbitMQ.Client;

namespace Bloom.Shared.Tests;

/// <summary>Test-side view of the broker: its own connection, never the library's.</summary>
public sealed class Rabbit : IAsyncDisposable
{
    private readonly IConnection _connection;

    public IChannel Channel { get; }

    private Rabbit(IConnection connection, IChannel channel) => (_connection, Channel) = (connection, channel);

    public static async Task<Rabbit> ConnectAsync(Containers containers)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(true, true));
        return new Rabbit(connection, channel);
    }

    /// <summary>Gets and acks one message, or null when the queue is empty.</summary>
    public Task<BasicGetResult?> BasicGetAsync(string queue) => Channel.BasicGetAsync(queue, autoAck: true);

    public Task PublishAsync(string exchange, string routingKey, byte[] body, IDictionary<string, object?>? headers = null) =>
        Channel.BasicPublishAsync(exchange, routingKey, mandatory: false,
            new BasicProperties { DeliveryMode = DeliveryModes.Persistent, Headers = headers }, body).AsTask();

    public async Task<uint> MessageCountAsync(string queue) => (await Channel.QueueDeclarePassiveAsync(queue)).MessageCount;

    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
