using Bloom.Launch.Data;
using Bloom.Launch.WaitingRoom;

namespace Bloom.Launch.Tests;

/// <summary>A product of a seeded launch: its purchase limit and one SKU per allocation (default: one SKU of 100).</summary>
public sealed record SeedProduct(int Limit, params int[] Allocations);

/// <param name="Skus">In product order.</param>
public sealed record SeededLaunch(Guid Id, IReadOnlyList<Guid> Products, IReadOnlyList<Guid> Skus);

public static class Seed
{
    private static readonly SeedProduct Default = new(2, 100);

    /// <summary>
    /// Inserts a launch with its products and SKUs. Times are the app clock plus the offset, truncated to whole microseconds
    /// (PostgreSQL's precision). Defaults: launch 1 minute ago, release in 7 days; for <c>announced</c> and <c>draft</c> the launch is in 1 hour.
    /// </summary>
    public static async Task<SeededLaunch> LaunchAsync(TestLaunch t, string stage = LaunchStages.PreorderOpen, TimeSpan? launchIn = null, TimeSpan? releaseIn = null, int rate = 200, params SeedProduct[] products)
    {
        var now = t.Clock.GetUtcNow();
        now = new DateTimeOffset(now.Ticks / 10 * 10, TimeSpan.Zero);
        var upcoming = stage is LaunchStages.Announced or LaunchStages.Draft;
        var row = new LaunchRow
        {
            Id = Guid.CreateVersion7(),
            Name = $"Launch {Guid.NewGuid():N}",
            Stage = stage,
            LaunchTime = now + (launchIn ?? (upcoming ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(-1))),
            ReleaseDate = now + (releaseIn ?? TimeSpan.FromDays(7)),
            AdmissionRate = rate,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var productIds = new List<Guid>();
        var skuIds = new List<Guid>();
        await using var db = t.NewDb();
        db.Launches.Add(row);
        foreach (var product in products.Length == 0 ? [Default] : products)
        {
            var productId = Guid.CreateVersion7();
            productIds.Add(productId);
            db.LaunchProducts.Add(new() { LaunchId = row.Id, ProductId = productId, PurchaseLimit = product.Limit });
            foreach (var allocation in product.Allocations)
            {
                var skuId = Guid.CreateVersion7();
                skuIds.Add(skuId);
                db.LaunchSkus.Add(new() { LaunchId = row.Id, SkuId = skuId, ProductId = productId, Allocation = allocation });
            }
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new(row.Id, productIds, skuIds);
    }

    /// <summary>Writes the pass key (<c>passId|expiresAtUnixMs</c>, TTL <paramref name="ttl"/>, default 10 minutes) and the <c>passes</c> member as the admission script does.</summary>
    public static async Task<Guid> PassAsync(TestLaunch t, Guid launch, Guid account, TimeSpan? ttl = null)
    {
        var passId = Guid.CreateVersion7();
        var life = ttl ?? TimeSpan.FromMinutes(10);
        var expires = t.Clock.GetUtcNow().Add(life).ToUnixTimeMilliseconds();
        await t.Redis.StringSetAsync(RedisKeys.Pass(launch, account), $"{passId:D}|{expires}", life);
        await t.Redis.SortedSetAddAsync(RedisKeys.Passes(launch), passId.ToString("D"), expires);
        return passId;
    }
}
