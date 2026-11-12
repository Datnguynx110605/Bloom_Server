using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Launch.Domain;
using Bloom.Launch.Observability;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Consumers;

/// <summary>
/// <c>launch.release-allocation-hold.v1</c> (01-launch §5.3, §6.6), in <c>ConsumerHost</c>'s transaction: a <c>held</c> or <c>sold</c> hold becomes <c>released</c> and its units return to the pool.
/// Every command, a no-op too, answers with <c>launch.hold-released.v1</c> so order's saga completes. The room is never reopened (§8).
/// </summary>
public sealed class ReleaseAllocationHoldConsumer(LaunchDbContext db, OutboxWriter outbox, LaunchMetrics metrics) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Command(LaunchMessages.ReleaseAllocationHold)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var orderId = envelope.GetPayload<ReleaseAllocationHoldV1>().OrderId;

        // The row lock decides against a concurrent order.paid for the same hold: the later one re-reads the status the earlier one committed.
        var hold = (await db.Database.SqlQuery<Locked>($"""
            SELECT id AS "Id", launch_id AS "LaunchId", account_id AS "AccountId", status AS "Status"
            FROM holds WHERE order_id = {orderId} AND status IN ({HoldStatuses.Held}, {HoldStatuses.Sold}) FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();
        if (hold is not null)
        {
            var rows = await db.Database.ExecuteSqlAsync(
                $"UPDATE holds SET status = {HoldStatuses.Released}, version = version + 1, updated_at = now() WHERE id = {hold.Id} AND status = {hold.Status}", ct);
            if (rows != 1) throw new InvalidOperationException($"Hold {hold.Id} changed under its row lock.");
            var wasHeld = hold.Status == HoldStatuses.Held;
            await HoldCounters.MoveAsync(db, metrics, hold, heldSign: wasHeld ? -1 : 0, soldSign: wasHeld ? 0 : -1, ct);
        }
        outbox.PublishEvent(LaunchMessages.HoldReleased, new HoldReleasedV1(orderId));
    }
}
