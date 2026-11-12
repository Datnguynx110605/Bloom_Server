using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Bloom.Shared.Messaging;

/// <summary>
/// One shared connection per process. Automatic recovery is off (R7): a closed connection is replaced here on
/// the next call, and callers re-open their own channels after a failure.
/// </summary>
internal sealed class RabbitMqConnection(BloomOptions options) : IAsyncDisposable
{
    /// <summary>How long a publish waits for its broker confirm (00-reliability §7.2, §7.3, §7.7).</summary>
    internal static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            var stale = _connection;
            _connection = null; // cleared first: a failing dispose must not leave the dead connection in place
            if (stale is not null)
            {
                try { await stale.DisposeAsync(); }
                catch { /* already closed; nothing left to release */ }
            }
            _connection = await new ConnectionFactory
            {
                Uri = new Uri(options.RabbitMqUri),
                AutomaticRecoveryEnabled = false,
                ClientProvidedName = options.ServiceName,
            }.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IChannel> CreateConfirmChannelAsync(CancellationToken ct) =>
        await (await GetAsync(ct)).CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);

    /// <summary>Message count of <paramref name="queue"/>, or 0 when it is not declared. Own channel: a passive declare of a missing queue closes it with 404.</summary>
    public async Task<uint> QueueDepthAsync(string queue, CancellationToken ct)
    {
        await using var channel = await (await GetAsync(ct)).CreateChannelAsync(cancellationToken: ct);
        try
        {
            return (await channel.QueueDeclarePassiveAsync(queue, ct)).MessageCount;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            return 0;
        }
    }

    /// <summary>Republishes the delivery (body, properties, headers after <paramref name="editHeaders"/>) through the default exchange, persistent, and awaits the confirm.</summary>
    internal static async Task RepublishAsync(
        IChannel channel, string queue, BasicDeliverEventArgs delivery, Action<Dictionary<string, object?>> editHeaders, CancellationToken ct)
    {
        var headers = delivery.BasicProperties.Headers is { } h ? new Dictionary<string, object?>(h) : [];
        editHeaders(headers);
        var properties = new BasicProperties(delivery.BasicProperties) { Headers = headers, DeliveryMode = DeliveryModes.Persistent };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConfirmTimeout);
        await channel.BasicPublishAsync("", queue, mandatory: false, properties, delivery.Body, timeout.Token);
    }

    /// <summary>A header as text: AMQP long strings arrive as bytes.</summary>
    internal static string? HeaderString(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var v)
            ? v switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string s => s, _ => null }
            : null;

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
