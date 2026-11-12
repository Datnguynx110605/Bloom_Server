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
    private const int Batch = 100;

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
                .OrderBy(p => p.CreatedAt).Take(Batch).Select(p => new { p.Id, p.StripeSessionId }).ToListAsync(ct)).Select(p => (p.Id, p.StripeSessionId!)).ToList();
            refunds = (await db.Refunds.AsNoTracking().Where(r => r.Status == RefundStatuses.Submitted && r.UpdatedAt < refundCutoff && r.StripeRefundId != null)
                .OrderBy(r => r.UpdatedAt).Take(Batch).Select(r => new { r.Id, r.StripeRefundId }).ToListAsync(ct)).Select(r => (r.Id, r.StripeRefundId!)).ToList();
            open = await db.Disputes.CountAsync(d => !ClosedDisputes.Contains(d.Status), ct);
        }

        foreach (var (id, sessionId) in payments)
            await FixAsync($"payment {id}", (provider, token) => provider.GetSessionAsync(sessionId, token), async (transitions, session, token) =>
                await transitions.ApplySessionAsync(id, session, token), ct);
        foreach (var (id, stripeRefundId) in refunds)
            await FixAsync($"refund {id}", (provider, token) => provider.GetRefundAsync(stripeRefundId, token), async (transitions, refund, token) =>
                await transitions.ApplyRefundAsync(id, refund.Status, refund.Id, refund.FailureReason, token)
                    ? refund.Status == "succeeded" ? "refund_succeeded" : "refund_failed"
                    : null, ct);

        metrics.SetDisputesOpen(open);
    }

    /// <summary>
    /// One record: <paramref name="fetch"/> asks Stripe first, with no transaction open (P-5), then <paramref name="apply"/> runs in its own transaction. The transitions lock the row and guard on its
    /// current status, so a row a webhook moved meanwhile is a no-op. <paramref name="apply"/> returns the fix kind, or null when nothing changed.
    /// </summary>
    private async Task FixAsync<T>(string record, Func<IPaymentProvider, CancellationToken, Task<T>> fetch, Func<Transitions, T, CancellationToken, Task<string?>> apply, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var stripe = await fetch(scope.ServiceProvider.GetRequiredService<IPaymentProvider>(), ct);
            var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var transitions = scope.ServiceProvider.GetRequiredService<Transitions>();
            var kind = await apply(transitions, stripe, ct);
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
