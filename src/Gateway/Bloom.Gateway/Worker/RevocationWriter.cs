using Bloom.Identity.Contracts;
using StackExchange.Redis;

namespace Bloom.Gateway;

/// <summary>Writes an account's revocation mark, <c>rev:{accountId}</c> (LLD 07 §4.1, §6.3).</summary>
public sealed class RevocationWriter(IConnectionMultiplexer redis)
{
    // The mark never goes down, so a late or duplicate message changes nothing (idempotent, order-independent).
    private const string Script = """
        local current = redis.call('GET', KEYS[1])
        local mark = ARGV[1]
        if current and tonumber(current) > tonumber(mark) then mark = current end
        return redis.call('SET', KEYS[1], mark, 'PX', ARGV[2])
        """;

    private const int TtlMilliseconds = 16 * 60 * 1000; // a token lives 15 min, so a mark older than that is no longer needed

    /// <exception cref="RedisException">Redis is unreachable (a timeout surfaces as <see cref="TimeoutException"/>).</exception>
    public Task WriteAsync(TokensRevokedV1 message) =>
        redis.GetDatabase().ScriptEvaluateAsync(Script, [$"rev:{message.AccountId:D}"], [message.IssuedBefore, TtlMilliseconds]);
}
