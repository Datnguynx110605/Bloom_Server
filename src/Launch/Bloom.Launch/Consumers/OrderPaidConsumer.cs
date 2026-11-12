using Bloom.Launch.Data;
using Bloom.Launch.Domain;
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
        var moved = (await db.Database.SqlQuery<Locked>($"""
            UPDATE holds SET status = {HoldStatuses.Sold}, version = version + 1, updated_at = now()
            WHERE order_id = {paid.OrderId} AND status = {HoldStatuses.Held}
            RETURNING id AS "Id", launch_id AS "LaunchId", account_id AS "AccountId", status AS "Status"
            """).ToListAsync(ct)).SingleOrDefault();
        if (moved is null) return;

        await HoldCounters.MoveAsync(db, metrics, moved, heldSign: -1, soldSign: 1, ct);
    }
}
