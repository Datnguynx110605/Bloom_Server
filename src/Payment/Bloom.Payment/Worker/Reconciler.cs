using Bloom.Payment.Data;
using Bloom.Payment.Domain;
using Bloom.Payment.Observability;
using Bloom.Payment.Stripe;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Worker;

/// <summary>
/// Every 5 min (04-payment §6.5, spec §7, plan R13): repairs missed webhooks with the webhook's own <see cref="Transitions"/>, so a later webhook is a no-op. <c>open</c> payments created more
/// than 15 min ago and <c>submitted</c> refunds last updated more than 10 min ago are retrieved from Stripe and applied. One Stripe call and one transaction per record: a Stripe error is a
/// warning and the record waits for the next pass.
/// </summary>
public sealed class Reconciler(IServiceScopeFactory scopes, TimeProvider clock, PaymentMetrics metrics, ILogger<Reconciler> logger) : BackgroundService
{
    private static readonly string[] ClosedDisputes = ["won", "lost", "warning_closed"];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Reconciler pass failed; retrying in 5 min");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        List<(Guid Id, string SessionId)> payments;
        List<(Guid Id, string StripeRefundId)> refunds;
        int open;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            var paymentCutoff = now.AddMinutes(-15);
            var refundCutoff = now.AddMinutes(-10);
            payments = (await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatuses.Open && p.CreatedAt < paymentCutoff && p.StripeSessionId != null)
                .OrderBy(p => p.CreatedAt).Select(p => new { p.Id, p.StripeSessionId }).ToListAsync(ct)).Select(p => (p.Id, p.StripeSessionId!)).ToList();
            refunds = (await db.Refunds.AsNoTracking().Where(r => r.Status == RefundStatuses.Submitted && r.UpdatedAt < refundCutoff && r.StripeRefundId != null)
                .OrderBy(r => r.UpdatedAt).Select(r => new { r.Id, r.StripeRefundId }).ToListAsync(ct)).Select(r => (r.Id, r.StripeRefundId!)).ToList();
            open = await db.Disputes.CountAsync(d => !ClosedDisputes.Contains(d.Status), ct);
        }

        foreach (var (id, sessionId) in payments)
            await FixAsync($"payment {id}", async (provider, transitions, token) =>
                await transitions.ApplySessionAsync(id, await provider.GetSessionAsync(sessionId, token), token), ct);
        foreach (var (id, stripeRefundId) in refunds)
            await FixAsync($"refund {id}", async (provider, transitions, token) =>
            {
                var refund = await provider.GetRefundAsync(stripeRefundId, token);
                return await transitions.ApplyRefundAsync(id, refund.Status, refund.Id, refund.FailureReason, token)
                    ? refund.Status == "succeeded" ? "refund_succeeded" : "refund_failed"
                    : null;
            }, ct);

        metrics.SetDisputesOpen(open);
    }

    /// <summary>One record: Stripe call first (no row is locked yet), then the transition in its own transaction. <paramref name="apply"/> returns the fix kind, or null when nothing changed.</summary>
    private async Task FixAsync(string record, Func<IPaymentProvider, Transitions, CancellationToken, Task<string?>> apply, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var kind = await apply(scope.ServiceProvider.GetRequiredService<IPaymentProvider>(), scope.ServiceProvider.GetRequiredService<Transitions>(), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (kind is not null) metrics.ReconcileFix(kind);
        }
        catch (ProviderException e)
        {
            logger.LogWarning(e, "Stripe lookup for {Record} failed; skipped until the next pass", record);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError(e, "Reconciling {Record} failed; skipped until the next pass", record);
        }
    }
}
