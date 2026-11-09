using System.Collections.Concurrent;
using System.Text.Json;
using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Bloom.Launch.WaitingRoom;

/// <summary>The derived waiting-room states of 01-launch §6.1.</summary>
public static class RoomStates
{
    public const string NotOpen = "not_open";
    public const string Open = "open";
    public const string SoldOut = "sold_out";
    public const string Closed = "closed";
}

/// <summary>The public view of a launch, <c>waiting_room_closed_at</c> and the sold-out flag (spec §7), as cached in <c>wr:{L}:state</c>.</summary>
public sealed record LaunchSnapshot(
    Guid Id, string Name, string Stage, DateTimeOffset LaunchTime, DateTimeOffset ReleaseDate, DateTimeOffset? WaitingRoomClosedAt, bool SoldOut,
    IReadOnlyList<LaunchProductV1> Products, IReadOnlyList<LaunchSkuV1> Skus)
{
    /// <summary>01-launch §6.1 at <paramref name="now"/> (the app clock).</summary>
    public string RoomState(DateTimeOffset now) =>
        WaitingRoomClosedAt is not null || Stage == LaunchStages.Released ? RoomStates.Closed
        : Stage == LaunchStages.Draft || (Stage == LaunchStages.Announced && now < LaunchTime - OpensBefore) ? RoomStates.NotOpen
        : SoldOut ? RoomStates.SoldOut
        : RoomStates.Open;

    private static readonly TimeSpan OpensBefore = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The launch snapshot in Redis (<c>wr:{L}:state</c>, 5 s; plan R15, spec §7). A miss loads it from Postgres, one load per pod per launch at a time,
/// so the request path reads Postgres at most once per pod per launch every 5 s (LR-04). A launch with no row is cached as <c>null</c> too.
/// A <see cref="RedisException"/> propagates: there is no Postgres fallback.
/// </summary>
public sealed class StateCache(IConnectionMultiplexer redis, IServiceScopeFactory scopes, ILogger<StateCache> logger)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<Guid, Lazy<Task<LaunchSnapshot?>>> _loads = [];

    /// <returns>Null when there is no such launch. A draft comes back as a snapshot; callers treat it as not found.</returns>
    public async Task<LaunchSnapshot?> GetAsync(Guid launchId, CancellationToken ct)
    {
        if (await ReadAsync(launchId) is { } hit) return hit.Snapshot;
        // GetOrAdd may run its factory twice under a race; the Lazy makes sure only one load starts.
        var load = _loads.GetOrAdd(launchId, id => new(() => LoadAsync(id)));
        return await load.Value.WaitAsync(ct);
    }

    /// <summary>Deletes the cached snapshot after a change commits. Best effort: the 5 s TTL bounds a miss.</summary>
    public async Task DropAsync(Guid launchId)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(RedisKeys.State(launchId));
        }
        catch (Exception e) when (e is RedisException or RedisTimeoutException)
        {
            logger.LogWarning(e, "Could not drop the cached state of launch {LaunchId}; it expires within {Seconds} s.", launchId, Ttl.TotalSeconds);
        }
    }

    private readonly record struct Hit(LaunchSnapshot? Snapshot);

    /// <returns>Null on a miss; otherwise the cached snapshot, itself null for a launch without a row.</returns>
    private async Task<Hit?> ReadAsync(Guid launchId)
    {
        var json = await redis.GetDatabase().StringGetAsync(RedisKeys.State(launchId));
        return json.IsNull ? null : new Hit(JsonSerializer.Deserialize<LaunchSnapshot?>((string)json!));
    }

    private async Task<LaunchSnapshot?> LoadAsync(Guid launchId)
    {
        try
        {
            // A caller that missed just before another load finished starts here: look again before going to Postgres.
            if (await ReadAsync(launchId) is { } hit) return hit.Snapshot;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LaunchDbContext>();
            var row = await db.Launches.AsNoTracking().Where(l => l.Id == launchId).Select(l => new
            {
                l.Name, l.Stage, l.LaunchTime, l.ReleaseDate, l.WaitingRoomClosedAt,
                Products = db.LaunchProducts.Where(p => p.LaunchId == l.Id).OrderBy(p => p.ProductId).Select(p => new { p.ProductId, p.PurchaseLimit }).ToList(),
                Skus = db.LaunchSkus.Where(s => s.LaunchId == l.Id).OrderBy(s => s.SkuId).Select(s => new { s.SkuId, s.ProductId, s.Allocation, s.Held, s.Sold }).ToList(),
            }).SingleOrDefaultAsync();
            var snapshot = row is null ? null : new LaunchSnapshot(
                launchId, row.Name, row.Stage, row.LaunchTime, row.ReleaseDate, row.WaitingRoomClosedAt,
                row.Skus.Count > 0 && row.Skus.All(s => (long)s.Held + s.Sold == s.Allocation),
                [.. row.Products.Select(p => new LaunchProductV1(p.ProductId, p.PurchaseLimit))],
                [.. row.Skus.Select(s => new LaunchSkuV1(s.SkuId, s.ProductId))]);
            await redis.GetDatabase().StringSetAsync(RedisKeys.State(launchId), JsonSerializer.Serialize(snapshot), Ttl);
            return snapshot;
        }
        finally
        {
            _loads.TryRemove(launchId, out _);
        }
    }
}
