using StackExchange.Redis;

namespace Bloom.Notification.Sockets;

/// <summary>Every API pod aborts its own sockets of the account named on <see cref="Disconnects.Channel"/>.</summary>
public sealed class DisconnectListener(IConnectionMultiplexer redis, ConnectionRegistry registry) : IHostedService
{
    private static readonly RedisChannel Channel = RedisChannel.Literal(Disconnects.Channel);

    public Task StartAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().SubscribeAsync(Channel, (_, message) =>
        {
            if (Guid.TryParseExact(message.ToString(), "D", out var accountId)) registry.AbortAll(accountId);
        });

    public Task StopAsync(CancellationToken cancellationToken) => redis.GetSubscriber().UnsubscribeAsync(Channel);
}
