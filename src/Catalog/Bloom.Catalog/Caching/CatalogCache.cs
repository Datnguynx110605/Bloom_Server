using System.Security.Cryptography;
using Google.Protobuf;
using StackExchange.Redis;

namespace Bloom.Catalog.Caching;

/// <param name="Value">Null on a miss or a failed read.</param>
/// <param name="Failed">Redis failed (down, slow); the caller reads Postgres and skips the SET.</param>
public readonly record struct CacheRead<T>(T? Value, bool Failed) where T : class;

/// <summary>
/// Cache-aside store of 08-catalog §6.3 (plan R10). Redis being down never fails a caller: reads miss, writes are skipped,
/// invalidation is logged and the entries expire after the 10 minute TTL.
/// </summary>
public sealed class CatalogCache(IConnectionMultiplexer redis, ILogger<CatalogCache> logger)
{
    public const string CategoriesKey = "categories";
    private const string VersionKey = "catalog:version";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// <summary><c>RedisTimeoutException</c> (a connected but slow Redis) derives from <see cref="TimeoutException"/>, not <see cref="RedisException"/>.</summary>
    private static bool IsRedisFailure(Exception e) => e is RedisException or TimeoutException;

    public static string ProductKey(Guid id) => $"product:{id}";

    /// <summary>Search keys carry the listings version, so a version bump retires every old page at once. Callers sort <c>option_value_ids</c> first.</summary>
    public static string SearchKey(long version, IMessage request) =>
        $"search:{version}:{Convert.ToHexStringLower(SHA256.HashData(request.ToByteArray()))}";

    /// <summary>A failed read is a miss that must not be followed by a SET (plan R10): callers check <see cref="CacheRead{T}.Failed"/>.</summary>
    public async Task<CacheRead<T>> GetAsync<T>(string key, MessageParser<T> parser) where T : class, IMessage<T>
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return new(value.IsNull ? null : parser.ParseFrom((byte[])value!), false);
        }
        catch (Exception e) when (IsRedisFailure(e)) { return new(null, true); }
    }

    public async Task SetAsync(string key, IMessage value)
    {
        try { await redis.GetDatabase().StringSetAsync(key, value.ToByteArray(), Ttl); }
        catch (Exception e) when (IsRedisFailure(e)) { }
    }

    /// <summary>0 when Redis fails, so searches go to Postgres under a key nobody reads or fills.</summary>
    public async Task<long> CatalogVersionAsync()
    {
        try { return (long)await redis.GetDatabase().StringGetAsync(VersionKey); }
        catch (Exception e) when (IsRedisFailure(e)) { return 0; }
    }

    public async Task InvalidateAsync(IReadOnlyCollection<Guid> products, bool categories, bool listings)
    {
        try
        {
            var db = redis.GetDatabase();
            var keys = products.Select(p => (RedisKey)ProductKey(p)).ToList();
            if (categories) keys.Add(CategoriesKey);
            await Task.WhenAll(
                keys.Count > 0 ? db.KeyDeleteAsync([.. keys]) : Task.CompletedTask,
                listings ? db.StringIncrementAsync(VersionKey) : Task.CompletedTask);
        }
        catch (Exception e) when (IsRedisFailure(e))
        {
            logger.LogWarning(e, "Cache invalidation failed; the entries expire within {Minutes} minutes.", Ttl.TotalMinutes);
        }
    }
}
