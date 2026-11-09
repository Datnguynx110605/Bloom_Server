using Bloom.Launch.Data;
using Bloom.Launch.Observability;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Consumers;

/// <summary>
/// <c>order.paid.v1</c> (01-launch §5.3, §6.6), in <c>ConsumerHost</c>'s transaction: a <c>held</c> hold becomes <c>sold</c> and its units move from <c>held</c> to <c>sold</c>.
/// No launch, no hold, or a hold that is not <c>held</c> (a duplicate, or already released) is a no-op. The event's lines are not used: the hold's own are.
/// </summary>
public sealed class OrderPaidConsumer(LaunchDbContext db, LaunchMetrics metrics) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("order", OrderMessages.Paid)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var paid = envelope.GetPayload<OrderPaidV1>();
        if (paid.LaunchId is null) return;

        // The conditional update is the single winner against a duplicate and against a release of the same hold (a release that locked the row first leaves 0 rows here).
        var moved = (await db.Database.SqlQuery<Moved>($"""
            WITH moved AS (
                UPDATE holds SET status = {HoldStatuses.Sold}, version = version + 1, updated_at = now()
                WHERE order_id = {paid.OrderId} AND status = {HoldStatuses.Held}
                RETURNING id AS "Id", launch_id AS "LaunchId", account_id AS "AccountId")
            SELECT "Id", "LaunchId", "AccountId" FROM moved
            """).ToListAsync(ct)).SingleOrDefault();
        if (moved is null) return;

        await HoldCounters.MoveAsync(db, metrics, moved, heldSign: -1, soldSign: 1, ct);
    }
}

internal sealed record Moved(Guid Id, Guid LaunchId, Guid AccountId);

/// <summary>The counter moves of 01-launch §6.6, in lock order (plan R9): products ascending, then SKUs ascending. Quantities come from the hold's own lines.</summary>
internal static class HoldCounters
{
    private sealed record Use(Guid Id, int Qty);

    /// <summary>Adds <c>sign × quantity</c> to <c>held</c> and <c>sold</c> of every product's limit row and every SKU; then sets <c>allocation_available</c> for the SKUs.</summary>
    /// <remarks>ponytail: the gauge is set before the host commits; a rollback after this point leaves one stale value until the next change.</remarks>
    public static async Task MoveAsync(LaunchDbContext db, LaunchMetrics metrics, Moved hold, int heldSign, int soldSign, CancellationToken ct)
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
