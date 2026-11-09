using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Domain;

public static class StageEvents
{
    /// <summary>
    /// Adds <c>launch.stage-changed.v1</c> to the outbox (plan R10) from the launch row, all its products and all its SKUs as they are in the open transaction.
    /// The caller saves the changes first and calls this only from <c>announced</c> on.
    /// </summary>
    public static async Task WriteAsync(LaunchDbContext db, OutboxWriter outbox, Guid launchId, CancellationToken ct)
    {
        var launch = await db.Launches.AsNoTracking().SingleAsync(l => l.Id == launchId, ct);
        var products = await db.LaunchProducts.AsNoTracking().Where(p => p.LaunchId == launchId).OrderBy(p => p.ProductId)
            .Select(p => new LaunchProductV1(p.ProductId, p.PurchaseLimit)).ToListAsync(ct);
        var skus = await db.LaunchSkus.AsNoTracking().Where(s => s.LaunchId == launchId).OrderBy(s => s.SkuId)
            .Select(s => new LaunchSkuV1(s.SkuId, s.ProductId)).ToListAsync(ct);
        outbox.PublishEvent(LaunchMessages.StageChanged, new LaunchStageChangedV1(launchId, launch.Stage, launch.LaunchTime, launch.ReleaseDate, products, skus));
    }
}
