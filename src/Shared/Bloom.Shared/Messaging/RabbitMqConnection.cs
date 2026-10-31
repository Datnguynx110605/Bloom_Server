using RabbitMQ.Client;

namespace Bloom.Shared.Messaging;

/// <summary>
/// One shared connection per process. Automatic recovery is off (R7): a closed connection is replaced here on
/// the next call, and callers re-open their own channels after a failure.
/// </summary>
internal sealed class RabbitMqConnection(BloomOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            if (_connection is not null) await _connection.DisposeAsync();
            _connection = null;
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

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
