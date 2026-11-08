using Bloom.Payment.Data;
using Bloom.Payment.Domain;
using Bloom.Payment.Stripe;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Worker;

/// <summary>
/// Makes the Stripe calls that commands record (04-payment §6.3; plan R12), every 5 s, up to 50 items per pass. Each item is handled in its own transaction that holds the row
/// <c>FOR UPDATE SKIP LOCKED</c>, the Stripe call included (fulfillment's <c>LabelWorker</c> pattern): a webhook for the same refund waits on the lock and then finds it settled.
/// </summary>
public sealed class ProviderExecutor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ProviderExecutor> logger) : BackgroundService
{
    // Retry delay after transient failure n (1-based); the 5th failure fails the refund (spec §7).
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private const int MaxAttempts = 5;
    private const int Batch = 50;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Provider executor pass failed; retrying in 5 s");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One pass: due <c>pending</c> refunds, oldest first, then <c>open</c> payments with a cancel request.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        // A failed refund is rescheduled into the future, so it is not picked again in this pass.
        for (var handled = 0; handled < Batch && await RefundOneAsync(ct); handled++) { }
        // A cancellation has no schedule, so each one is tried at most once per pass (spec §7: the next tick retries).
        var tried = new List<Guid>();
        while (tried.Count < Batch && await CancelOneAsync(tried, ct)) { }
    }

    /// <summary>
    /// Expires the session of one cancel-requested payment (04-payment §6.4, spec §7). Success → <c>canceled</c>; transient error → unchanged, next tick; permanent error (the session is no
    /// longer open) → retrieve it and apply its state, so a session paid in the same moment ends <c>succeeded</c> at once (owner item 4, PR-01). False when nothing is left to try.
    /// </summary>
    private async Task<bool> CancelOneAsync(List<Guid> tried, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var skip = tried.ToArray();
        var payment = (await db.Payments.FromSql($"""
            SELECT * FROM payments
            WHERE status = {PaymentStatuses.Open} AND cancel_requested AND session_expires_at > {now} AND id <> ALL({skip})
            ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (payment is null) return false;
        tried.Add(payment.Id);

        try
        {
            var provider = scope.ServiceProvider.GetRequiredService<IPaymentProvider>();
            var transitions = scope.ServiceProvider.GetRequiredService<Transitions>();
            var sessionId = payment.StripeSessionId ?? throw new InvalidOperationException($"Open payment {payment.Id} has no session.");
            try
            {
                await provider.ExpireSessionAsync(sessionId, ct);
                await transitions.CancelAsync(payment.Id, ct);
            }
            catch (ProviderException e) when (!e.Transient)
            {
                await transitions.ApplySessionAsync(payment.Id, await provider.GetSessionAsync(sessionId, ct), ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (ProviderException e)
        {
            logger.LogWarning(e, "Stripe cancellation of payment {PaymentId} failed; retrying on the next tick", payment.Id);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError(e, "Cancellation of payment {PaymentId} failed; retrying on the next tick", payment.Id);
        }
        return true; // the transaction rolls back on dispose when it did not commit, which releases the row
    }

    private async Task<bool> RefundOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var refund = (await db.Refunds.FromSql($"""
            SELECT * FROM refunds
            WHERE status = {RefundStatuses.Pending} AND next_attempt_at <= {now}
            ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (refund is null) return false;

        try
        {
            var transitions = scope.ServiceProvider.GetRequiredService<Transitions>();
            try
            {
                var intent = await db.Payments.AsNoTracking().Where(p => p.Id == refund.PaymentId).Select(p => p.StripePaymentIntentId).SingleAsync(ct)
                    ?? throw new InvalidOperationException($"Payment {refund.PaymentId} of refund {refund.Id} has no payment intent.");
                var created = await scope.ServiceProvider.GetRequiredService<IPaymentProvider>().CreateRefundAsync(refund.Id, refund.OrderId, intent, refund.AmountCents, ct);
                await transitions.ApplyRefundAsync(refund.Id, created.Status, created.Id, created.FailureReason, ct);
            }
            catch (ProviderException e) when (!e.Transient)
            {
                await transitions.ApplyRefundAsync(refund.Id, "failed", null, e.Reason, ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Any other failure of this row is a failed attempt, so a poison row backs off and the next row is still served. The row lock is released first.
            await tx.RollbackAsync(CancellationToken.None);
            await FailAsync(refund, now, e, ct);
        }
        return true;
    }

    private async Task FailAsync(RefundRow refund, DateTimeOffset now, Exception e, CancellationToken ct)
    {
        var n = refund.Attempts + 1;
        var next = now + Backoff[Math.Min(n, Backoff.Length) - 1];
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE refunds SET attempts = {n}, next_attempt_at = {next}, updated_at = {now}
            WHERE id = {refund.Id} AND status = {RefundStatuses.Pending}
            """, ct);
        if (n >= MaxAttempts)
        {
            await scope.ServiceProvider.GetRequiredService<Transitions>().ApplyRefundAsync(refund.Id, "failed", null, ProviderException.Unavailable, ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (n < MaxAttempts) logger.LogWarning(e, "Stripe refund for {RefundId} failed (attempt {Attempt}); retrying at {NextAttemptAt}", refund.Id, n, next);
    }
}
