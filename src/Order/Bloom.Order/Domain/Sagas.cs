using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Data;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Domain;

/// <summary>Order sagas (plan R12, spec §7): the saga row and the commands it waits for go out in the caller's transaction (OR-02).</summary>
public sealed class Sagas(OrderDbContext db, OutboxWriter outbox, TimeProvider clock)
{
    /// <summary>
    /// Inserts the saga <paramref name="type"/> of the order (<c>ON CONFLICT DO NOTHING</c>) and sends the commands of R12 for the order's <paramref name="kind"/>.
    /// False when the saga already exists: it ran before, so nothing is sent again. The commands stay in the change tracker until the caller's <c>SaveChanges</c>.
    /// </summary>
    /// <param name="afterPayment">A launch order paid already also owns an inventory reservation (the backorder or commitment) besides its allocation hold.</param>
    /// <param name="releaseReason">The order's cancel reason, sent with the launch release (owner item 4).</param>
    public async Task<bool> StartAsync(Guid orderId, string type, string kind, bool afterPayment, string releaseReason, CancellationToken ct)
    {
        var steps = StepsFor(type, kind, afterPayment);
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO order_sagas (order_id, type, pending_steps, started_at) VALUES ({orderId}, {type}, {steps}, {clock.GetUtcNow()})
            ON CONFLICT DO NOTHING
            """, ct);
        if (inserted == 0) return false;

        if (steps.Contains(SagaSteps.LaunchRelease))
            outbox.SendCommand("launch", LaunchMessages.ReleaseAllocationHold, new ReleaseAllocationHoldV1(orderId, releaseReason));
        if (steps.Contains(SagaSteps.InventoryRelease))
            outbox.SendCommand("inventory", InventoryMessages.ReleaseReservation, new ReleaseReservationV1(orderId));
        if (steps.Contains(SagaSteps.Refund))
        {
            var total = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.TotalCents).SingleAsync(ct);
            outbox.SendCommand("payment", PaymentMessages.RefundPayment,
                new RefundPaymentV1(orderId, Guid.CreateVersion7(), total, type == SagaTypes.LatePayment ? "late_payment" : "cancellation", null));
        }
        if (type == SagaTypes.Expiry)
            outbox.SendCommand("payment", PaymentMessages.CancelPayment, new CancelPaymentV1(orderId)); // no step: payment does not confirm it
        return true;
    }

    /// <summary>A completion event: removes <paramref name="step"/> from every open saga of the order that waits for it; an empty list completes the saga.</summary>
    public async Task CompleteStepAsync(Guid orderId, string step, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE order_sagas
            SET pending_steps = array_remove(pending_steps, {step}),
                completed_at = CASE WHEN cardinality(array_remove(pending_steps, {step})) = 0 THEN {clock.GetUtcNow()} ELSE completed_at END
            WHERE order_id = {orderId} AND completed_at IS NULL AND {step} = ANY(pending_steps)
            """, ct);

    /// <summary><c>payment.refund-failed.v1</c> (spec §4): <c>refund</c> becomes <c>refund_failed</c> in the open saga that waits for it, so the saga stays open for an operator.</summary>
    public async Task FailRefundAsync(Guid orderId, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE order_sagas SET pending_steps = array_replace(pending_steps, {SagaSteps.Refund}, {SagaSteps.RefundFailed})
            WHERE order_id = {orderId} AND completed_at IS NULL AND {SagaSteps.Refund} = ANY(pending_steps)
            """, ct);

    private static string[] StepsFor(string type, string kind, bool afterPayment)
    {
        if (type == SagaTypes.LatePayment) return [SagaSteps.Refund];
        var steps = new List<string>();
        if (kind == OrderKinds.Launch) steps.Add(SagaSteps.LaunchRelease);
        if (kind == OrderKinds.Regular || afterPayment) steps.Add(SagaSteps.InventoryRelease);
        if (type == SagaTypes.Cancellation) steps.Add(SagaSteps.Refund);
        return [.. steps];
    }
}
