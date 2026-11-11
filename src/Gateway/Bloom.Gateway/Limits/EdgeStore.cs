using System.Reflection;
using StackExchange.Redis;

namespace Bloom.Gateway;

/// <param name="Revoked">The token was issued before the account's revocation mark.</param>
/// <param name="Allowed">False when revoked, or when <paramref name="DeniedPolicy"/> refused the request.</param>
public sealed record EdgeDecision(bool Revoked, bool Allowed, TimeSpan RetryAfter, string? DeniedPolicy);

/// <summary>Revocation and rate limits in one Redis round trip (LLD 07 §6.2).</summary>
public sealed class EdgeStore(IConnectionMultiplexer redis, TimeProvider time, IReadOnlyDictionary<string, RateLimitPolicy> policies)
{
    private static readonly LuaScript Script = LuaScript.Prepare(ReadScript());

    // Loaded on first use, not at startup: the gateway starts while Redis is down (P16). Two first requests may both load; that is harmless.
    private LoadedLuaScript? _loaded;

    /// <param name="caller">Null when anonymous.</param>
    /// <param name="routePolicy">Null when the route has no policy, or the route is public-optional-user and the caller anonymous.</param>
    /// <exception cref="RedisException">Redis is unreachable (a timeout surfaces as <see cref="TimeoutException"/>).</exception>
    public async Task<EdgeDecision> CheckAsync(Caller? caller, string clientIp, RateLimitPolicy? routePolicy, string requestId)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var keys = new List<RedisKey>();
        if (caller is not null)
            keys.Add($"rev:{caller.UserId:D}");

        var global = policies["global"];
        var globalLimit = routePolicy?.Name == "webhook" ? 0 : global.Limit; // webhooks are exempt (LLD 07 §4.2)
        if (globalLimit > 0)
            keys.Add(FixedWindowKey(global, clientIp, now));

        RedisValue[] route = ["", "", 0, 0];
        if (routePolicy is not null)
        {
            var subject = routePolicy.Subject(caller, clientIp);
            (var algorithm, var key, var first, var second) = routePolicy.Algorithm switch
            {
                Algorithm.FixedWindow => ("fixed", FixedWindowKey(routePolicy, subject, now), routePolicy.Limit, (double)routePolicy.WindowSeconds * 1000),
                Algorithm.SlidingWindow => ("sliding", $"sw:{routePolicy.Name}:{subject}", routePolicy.Limit, (double)routePolicy.WindowSeconds * 1000),
                _ => ("bucket", $"tb:{routePolicy.Name}:{subject}", routePolicy.Burst, routePolicy.PerMinute),
            };
            keys.Add(key);
            route = [algorithm, routePolicy.Name, first, second];
        }

        RedisValue[] args = [now, caller is null ? RedisValue.EmptyString : caller.IssuedAt * 1000, requestId, globalLimit, global.WindowSeconds * 1000, .. route];
        var result = (RedisResult[])(await EvaluateAsync(keys.ToArray(), args))!;
        var denied = (string?)result[3];
        return new((int)result[0] == 1, (int)result[1] == 1, TimeSpan.FromMilliseconds((long)result[2]), denied is { Length: > 0 } ? denied : null);
    }

    /// <summary>EVALSHA of the loaded script; after Redis lost its script cache (a restart) the script is loaded again, once per call.</summary>
    private async Task<RedisResult> EvaluateAsync(RedisKey[] keys, RedisValue[] args)
    {
        var db = redis.GetDatabase();
        var script = _loaded ??= await Script.LoadAsync(redis.GetServers()[0]);
        try
        {
            return await db.ScriptEvaluateAsync(script.Hash, keys, args);
        }
        catch (RedisServerException e) when (e.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            script = _loaded = await Script.LoadAsync(redis.GetServers()[0]);
            return await db.ScriptEvaluateAsync(script.Hash, keys, args);
        }
    }

    // fw:{policy}:{subject}:{floor(now / window)} (plan P18)
    private static string FixedWindowKey(RateLimitPolicy policy, string subject, long now) =>
        $"fw:{policy.Name}:{subject}:{now / (policy.WindowSeconds * 1000L)}";

    private static string ReadScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("edge.lua")!;
        return new StreamReader(stream).ReadToEnd();
    }
}
