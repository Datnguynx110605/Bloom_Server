using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Observability;
using Bloom.Payment.Stripe;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Domain;

/// <summary>
/// Payment status changes shared by the webhook, the executor and the reconcilers (04-payment §6.5). Each is one guarded statement (plan R8) that runs in the caller's
/// transaction and writes outbox rows to the caller's context: the caller saves and commits.
/// </summary>
public sealed class Transitions(PaymentDbContext db, OutboxWriter outbox, TimeProvider clock, PaymentMetrics metrics)
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
    public async Task<bool> ExpireAsync(Guid paymentId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return await db.Database.ExecuteSqlAsync($"""
            UPDATE payments SET status = 'expired', version = version + 1, updated_at = {now}
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

    private sealed class Succeeded
    {
        public string PriorStatus { get; set; } = "";
        public bool PriorCancelRequested { get; set; }
        public Guid OrderId { get; set; }
        public long AmountCents { get; set; }
    }
}
