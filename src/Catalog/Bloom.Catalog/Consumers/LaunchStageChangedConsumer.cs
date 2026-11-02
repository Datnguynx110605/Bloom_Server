using Bloom.Catalog.Caching;
using Bloom.Catalog.Data;
using Bloom.Launch.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Consumers;

/// <summary>08-catalog §6.4, plan R23: stamps the launch on its products and SKUs, newest stage wins, <c>released</c> activates.</summary>
public sealed class LaunchStageChangedConsumer(CatalogDbContext db, CacheInvalidation cache, TimeProvider clock) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("launch", LaunchMessages.StageChanged)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var e = envelope.GetPayload<LaunchStageChangedV1>(); // before any side effect: an invalid payload dead-letters at once
        if (e.LaunchId == Guid.Empty || LaunchStages.Rank(e.Stage) < 0)
            throw new PermanentFailureException($"{LaunchMessages.StageChanged} has no launchId or an unknown stage '{e.Stage}'.");

        var wanted = e.Products.Select(p => p.ProductId).Concat(e.Skus.Select(s => s.ProductId)).Distinct().ToArray();
        var skuIds = e.Skus.Select(s => s.SkuId).Distinct().ToArray();
        var now = clock.GetUtcNow();

        // Product rows first, in id order, then SKU rows: the service-wide lock order (a concurrent admin write never deadlocks with this).
        var known = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM products WHERE id = ANY({wanted}) ORDER BY id FOR UPDATE").ToArrayAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE products SET launch_id = {e.LaunchId}, launch_stage = {e.Stage}, updated_at = {now},
                status = CASE WHEN {e.Stage} = 'released' THEN 'active' ELSE status END,
                published_at = CASE WHEN {e.Stage} = 'released' THEN COALESCE(published_at, {now}) ELSE published_at END
            WHERE id = ANY({known})
              AND (launch_stage IS NULL OR launch_id <> {e.LaunchId}
                   OR array_position({LaunchStages.All}, launch_stage) <= array_position({LaunchStages.All}, {e.Stage}))
              AND (launch_id IS DISTINCT FROM {e.LaunchId} OR launch_stage IS DISTINCT FROM {e.Stage}
                   OR ({e.Stage} = 'released' AND (status <> 'active' OR published_at IS NULL))) -- the same event twice changes nothing
            """, ct);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE skus SET launch_id = {e.LaunchId} WHERE id = ANY({skuIds}) AND product_id = ANY({known}) AND launch_id IS DISTINCT FROM {e.LaunchId}", ct);

        foreach (var id in known) cache.Product(id);
        if (known.Length > 0) cache.Listings();
    }
}
