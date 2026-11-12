using Bloom.Launch.Data;
using Bloom.Launch.Observability;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Domain;

/// <summary>A hold the settlement consumers act on: <see cref="Status"/> is the row's status as the consumer's update or row lock left it.</summary>
internal sealed record Locked(Guid Id, Guid LaunchId, Guid AccountId, string Status);

/// <summary>The counter moves of 01-launch §6.6, in lock order (plan R9): products ascending, then SKUs ascending. Quantities come from the hold's own lines.</summary>
internal static class HoldCounters
{
    private sealed record Use(Guid Id, int Qty);

    /// <summary>Adds <c>sign × quantity</c> to <c>held</c> and <c>sold</c> of every product's limit row and every SKU; then sets <c>allocation_available</c> for the SKUs.</summary>
    /// <remarks>ponytail: the gauge is set before the host commits; a rollback after this point leaves one stale value until the next change.</remarks>
    public static async Task MoveAsync(LaunchDbContext db, LaunchMetrics metrics, Locked hold, int heldSign, int soldSign, CancellationToken ct)
    {
        var products = await db.Database.SqlQuery<Use>($"SELECT product_id AS \"Id\", SUM(quantity)::int AS \"Qty\" FROM hold_lines WHERE hold_id = {hold.Id} GROUP BY product_id")
            .OrderBy(u => u.Id).ToListAsync(ct);
        foreach (var p in products)
        {
            var heldBy = heldSign * p.Qty;
            var soldBy = soldSign * p.Qty;
            var rows = await db.Database.ExecuteSqlAsync($"""
                UPDATE limit_usage SET held = held + {heldBy}, sold = sold + {soldBy}
                WHERE launch_id = {hold.LaunchId} AND account_id = {hold.AccountId} AND product_id = {p.Id}
                """, ct);
            if (rows != 1) throw new InvalidOperationException($"Hold {hold.Id} has no limit row for product {p.Id}.");
        }

        var skus = await db.Database.SqlQuery<Use>($"SELECT sku_id AS \"Id\", quantity AS \"Qty\" FROM hold_lines WHERE hold_id = {hold.Id}")
            .OrderBy(u => u.Id).ToListAsync(ct);
        foreach (var s in skus)
        {
            var heldBy = heldSign * s.Qty;
            var soldBy = soldSign * s.Qty;
            var left = await db.Database.SqlQuery<long>($"""
                UPDATE launch_skus SET held = held + {heldBy}, sold = sold + {soldBy}
                WHERE launch_id = {hold.LaunchId} AND sku_id = {s.Id}
                RETURNING allocation - held - sold AS "Value"
                """).ToListAsync(ct);
            if (left.Count != 1) throw new InvalidOperationException($"Hold {hold.Id} has no SKU row for {s.Id}.");
            metrics.SetAvailable(hold.LaunchId, s.Id, left[0]);
        }
    }
}
