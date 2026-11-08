using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Observability;
using Bloom.Payment.Stripe;
using Bloom.Payment.Worker;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Domain;

/// <summary>
/// Payment status changes shared by the webhook, the executor and the reconcilers (04-payment §6.5). Each is one guarded statement (plan R8) that runs in the caller's
/// transaction and writes outbox rows to the caller's context: the caller saves and commits.
/// </summary>
public sealed class Transitions(PaymentDbContext db, OutboxWriter outbox, TimeProvider clock, PaymentMetrics metrics, ILogger<Transitions> logger)
{
    /// <summary>Any status but <c>succeeded</c> → <c>succeeded</c>: money was taken, so an <c>expired</c> or <c>canceled</c> payment is recorded too. False when already <c>succeeded</c> or unknown.</summary>
    public async Task<bool> SucceedAsync(Guid paymentId, string paymentIntentId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // The CTE locks the row and keeps its prior values; EF wraps the statement in a SELECT, which PostgreSQL only allows around a WITH.
        var moved = (await db.Database.SqlQuery<Succeeded>($"""
            WITH old AS (SELECT id, status, cancel_requested FROM payments WHERE id = {paymentId} FOR UPDATE),
            moved AS (
                UPDATE payments p SET status = 'succeeded', stripe_payment_intent_id = {paymentIntentId}, succeeded_at = {now}, locked_until = NULL,
                    version = p.version + 1, updated_at = {now}
                FROM old WHERE p.id = old.id AND p.status <> 'succeeded'
                RETURNING old.status AS "PriorStatus", old.cancel_requested AS "PriorCancelRequested", p.order_id AS "OrderId", p.amount_cents AS "AmountCents")
            SELECT "PriorStatus", "PriorCancelRequested", "OrderId", "AmountCents" FROM moved
            """).ToListAsync(ct)).SingleOrDefault();
        if (moved is null) return false;

        outbox.PublishEvent(PaymentMessages.Succeeded, new PaymentSucceededV1(moved.OrderId, paymentId, moved.AmountCents, now));
        metrics.Succeeded(moved.PriorStatus is PaymentStatuses.Expired or PaymentStatuses.Canceled || moved.PriorCancelRequested);
        return true;
    }

    /// <summary><c>open</c> → <c>expired</c>, no event (order handles its own deadline). False for any other status.</summary>
    public Task<bool> ExpireAsync(Guid paymentId, CancellationToken ct) => LeaveOpenAsync(paymentId, PaymentStatuses.Expired, ct);

    /// <summary><c>open</c> → <c>canceled</c> after Stripe expired the session, no event. False for any other status.</summary>
    public Task<bool> CancelAsync(Guid paymentId, CancellationToken ct) => LeaveOpenAsync(paymentId, PaymentStatuses.Canceled, ct);

    private async Task<bool> LeaveOpenAsync(Guid paymentId, string to, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Database.ExecuteSqlAsync($"""
            UPDATE payments SET status = {to}, version = version + 1, updated_at = {now}
            WHERE id = {paymentId} AND status = 'open'
            """, ct) == 1;
    }

    /// <summary>Applies a Stripe session as the webhook would: <c>payment_succeeded</c>, <c>payment_expired</c>, or null when nothing changed.</summary>
    public async Task<string?> ApplySessionAsync(Guid paymentId, StripeSession session, CancellationToken ct)
    {
        if (session is { Status: "complete", PaymentStatus: "paid" })
            return await SucceedAsync(paymentId, session.PaymentIntentId ?? throw new InvalidOperationException($"Paid session {session.Id} has no payment intent."), ct) ? "payment_succeeded" : null;
        if (session.Status == "expired")
            return await ExpireAsync(paymentId, ct) ? "payment_expired" : null;
        return null;
    }

    /// <summary>
    /// Applies a Stripe refund status (spec §4 "Refund completion", §7): <c>succeeded</c> → <c>succeeded</c> + <c>refund-completed</c>; <c>failed</c> / <c>canceled</c> → <c>failed</c>, the amount is
    /// released from <c>refunded_cents</c>, <c>refund-failed</c> + error log (owner item 5); <c>pending</c> / <c>requires_action</c> → <c>submitted</c>. Locks <c>refunds</c> then <c>payments</c> (plan R8),
    /// so two callers settle a refund once. A final refund never changes: a contradicting status only logs an error. True when the status changed; false for an unknown refund too.
    /// </summary>
    public async Task<bool> ApplyRefundAsync(Guid refundId, string stripeStatus, string? stripeRefundId, string? failureReason, CancellationToken ct)
    {
        var refund = (await db.Refunds.FromSql($"SELECT * FROM refunds WHERE id = {refundId} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (refund is null) return false;
        if (stripeRefundId is not null && refund.StripeRefundId is null)
            await db.Database.ExecuteSqlAsync($"UPDATE refunds SET stripe_refund_id = {stripeRefundId} WHERE id = {refundId}", ct);

        var target = stripeStatus switch
        {
            "succeeded" => RefundStatuses.Succeeded,
            "failed" or "canceled" => RefundStatuses.Failed,
            "pending" or "requires_action" => RefundStatuses.Submitted,
            _ => null,
        };
        if (target is null)
        {
            logger.LogError("Stripe reports refund {RefundId} of order {OrderId} in the unknown status {Status}; ignored", refundId, refund.OrderId, stripeStatus);
            // A pending row would be due again at once and, as the oldest, block every other refund: look again after the longest backoff.
            var at = clock.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE refunds SET next_attempt_at = {at + ProviderExecutor.Backoff[^1]}, updated_at = {at}
                WHERE id = {refundId} AND status = {RefundStatuses.Pending}
                """, ct);
            return false;
        }
        if (refund.Status is RefundStatuses.Succeeded or RefundStatuses.Failed)
        {
            if (target != RefundStatuses.Submitted && target != refund.Status)
                logger.LogError("Stripe reports refund {RefundId} of order {OrderId} as {Status}, but it is already {Final}; unchanged", refundId, refund.OrderId, stripeStatus, refund.Status);
            return false;
        }
        if (target == RefundStatuses.Submitted && refund.Status != RefundStatuses.Pending) return false;

        var now = clock.GetUtcNow();
        var reason = target == RefundStatuses.Failed ? failureReason ?? stripeStatus : null;
        string[] from = target == RefundStatuses.Submitted ? [RefundStatuses.Pending] : [RefundStatuses.Pending, RefundStatuses.Submitted];
        if (await db.Database.ExecuteSqlAsync($"""
            UPDATE refunds SET status = {target}, failure_reason = {reason}, updated_at = {now}
            WHERE id = {refundId} AND status = ANY({from})
            """, ct) != 1)
            throw new InvalidOperationException($"Refund {refundId} left {refund.Status} while its row was locked."); // cannot happen

        if (target == RefundStatuses.Succeeded)
            outbox.PublishEvent(PaymentMessages.RefundCompleted, new RefundCompletedV1(refund.OrderId, refundId, refund.AmountCents, refund.Lines));
        else if (target == RefundStatuses.Failed)
        {
            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE payments SET refunded_cents = refunded_cents - {refund.AmountCents}, version = version + 1, updated_at = {now}
                WHERE id = {refund.PaymentId}
                """, ct) != 1)
                throw new InvalidOperationException($"Payment {refund.PaymentId} of refund {refundId} is missing."); // the foreign key rules this out
            outbox.PublishEvent(PaymentMessages.RefundFailed, new RefundFailedV1(refund.OrderId, refundId, reason!));
            logger.LogError("Refund {RefundId} of order {OrderId} failed: {Reason}; {Amount} cents released", refundId, refund.OrderId, reason, refund.AmountCents);
        }
        metrics.Refund(target);
        return true;
    }

    private sealed class Succeeded
    {
        public string PriorStatus { get; set; } = "";
        public bool PriorCancelRequested { get; set; }
        public Guid OrderId { get; set; }
        public long AmountCents { get; set; }
    }
}
