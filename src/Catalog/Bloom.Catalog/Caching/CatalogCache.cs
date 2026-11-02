using System.Security.Cryptography;
using Google.Protobuf;
using StackExchange.Redis;

namespace Bloom.Catalog.Caching;

/// <summary>
/// Cache-aside store of 08-catalog §6.3 (plan R10). Redis being down never fails a caller: reads miss, writes are skipped,
/// invalidation is logged and the entries expire after the 10 minute TTL.
/// </summary>
public sealed class CatalogCache(IConnectionMultiplexer redis, ILogger<CatalogCache> logger)
{
    public const string CategoriesKey = "categories";
    private const string VersionKey = "catalog:version";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static string ProductKey(Guid id) => $"product:{id}";

    /// <summary>Search keys carry the listings version, so a version bump retires every old page at once. Callers sort <c>option_value_ids</c> first.</summary>
    public static string SearchKey(long version, IMessage request) =>
        $"search:{version}:{Convert.ToHexStringLower(SHA256.HashData(request.ToByteArray()))}";

    /// <summary>Null on a miss or when Redis fails.</summary>
    public async Task<T?> GetAsync<T>(string key, MessageParser<T> parser) where T : class, IMessage<T>
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNull ? null : parser.ParseFrom((byte[])value!);
        }
        catch (RedisException) { return null; }
    }

    public async Task SetAsync(string key, IMessage value)
    {
        try { await redis.GetDatabase().StringSetAsync(key, value.ToByteArray(), Ttl); }
        catch (RedisException) { }
    }

    /// <summary>0 when Redis fails, so searches go to Postgres under a key nobody reads or fills.</summary>
    public async Task<long> CatalogVersionAsync()
    {
        try { return (long)await redis.GetDatabase().StringGetAsync(VersionKey); }
        catch (RedisException) { return 0; }
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
        catch (RedisException e)
        {
            logger.LogWarning(e, "Cache invalidation failed; the entries expire within {Minutes} minutes.", Ttl.TotalMinutes);
        }
    }
}
