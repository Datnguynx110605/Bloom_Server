using StackExchange.Redis;

namespace Bloom.Launch.WaitingRoom;

/// <summary>01-launch §6.4 step 7: the only code that pops the queue. Pop and pass record are one atomic script, so a worker crash cannot pop a customer without recording the pass.</summary>
public static class AdmissionScript
{
    public const int PassTtlSeconds = 600;

    // KEYS = [q, passes, tick]; ARGV = [pass key prefix, exp, ttl, pid…]. Returns the popped accounts, lowest score first.
    // The tick key paces admission per launch, not per replica (LR-06, LLD-ADR-012): a live key means another tick already admitted this second, so pop nothing.
    private const string Lua = """
        if not redis.call('SET', KEYS[3], '1', 'NX', 'PX', 1000) then return {} end
        local popped = redis.call('ZPOPMIN', KEYS[1], #ARGV - 3)
        local accounts = {}
        for i = 1, #popped, 2 do
          local pid = ARGV[3 + (i + 1) / 2]
          redis.call('SET', ARGV[1] .. popped[i], pid .. '|' .. ARGV[2], 'EX', ARGV[3])
          redis.call('ZADD', KEYS[2], ARGV[2], pid)
          accounts[#accounts + 1] = popped[i]
        end
        return accounts
        """;

    /// <returns>The admitted accounts in pop order. At most <paramref name="passIds"/>.Count of them: fewer when the queue runs out.</returns>
    public static async Task<IReadOnlyList<Guid>> RunAsync(IDatabase redis, Guid launchId, IReadOnlyList<Guid> passIds, long expiresAtUnixMs)
    {
        var prefix = RedisKeys.Pass(launchId, Guid.Empty)[..^36]; // "wr:{L}:pass:"; a "D" Guid is 36 chars
        RedisValue[] argv = [prefix, expiresAtUnixMs, PassTtlSeconds, .. passIds.Select(p => (RedisValue)p.ToString("D"))];
        var reply = (RedisResult[])(await redis.ScriptEvaluateAsync(Lua, [RedisKeys.Queue(launchId), RedisKeys.Passes(launchId), RedisKeys.Tick(launchId)], argv))!;
        return [.. reply.Select(r => Guid.Parse((string)r!))];
    }
}
