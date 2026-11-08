using System.Buffers.Text;
using System.Security.Cryptography;
using StackExchange.Redis;

namespace Bloom.Notification.Sockets;

/// <summary>Single-use WebSocket tickets: <c>ws:ticket:{ticket}</c> → account id, 30 s (09-notification §4.2, §5.1).</summary>
public sealed class Tickets(IConnectionMultiplexer redis)
{
    private static string Key(string ticket) => $"ws:ticket:{ticket}";

    public async Task<string> IssueAsync(Guid accountId)
    {
        var ticket = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await redis.GetDatabase().StringSetAsync(Key(ticket), accountId.ToString("D"), TimeSpan.FromSeconds(30));
        return ticket;
    }

    /// <summary>The account of a valid ticket, which is spent by this call (<c>GETDEL</c>); null when unknown, used or expired.</summary>
    public async Task<Guid?> RedeemAsync(string ticket)
    {
        var value = await redis.GetDatabase().StringGetDeleteAsync(Key(ticket));
        return Guid.TryParseExact(value, "D", out var accountId) ? accountId : null;
    }
}
