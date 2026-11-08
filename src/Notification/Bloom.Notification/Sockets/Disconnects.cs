using StackExchange.Redis;

namespace Bloom.Notification.Sockets;

/// <summary>The cross-pod disconnect command: the account id (<c>D</c>) on Redis channel <see cref="Channel"/> (spec §7 Sockets).</summary>
public static class Disconnects
{
    public const string Channel = "notification:disconnect";

    public static Task PublishAsync(IConnectionMultiplexer redis, Guid accountId) =>
        redis.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel), accountId.ToString("D"));
}
