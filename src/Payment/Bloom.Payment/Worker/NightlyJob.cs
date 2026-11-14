using Bloom.Payment.Data;
using Bloom.Payment.Observability;
using Bloom.Payment.Stripe;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Worker;

/// <summary>
/// Nightly at 02:00 UTC (04-payment §6.5, spec §7, plan R13): compares the previous UTC day's succeeded Stripe PaymentIntents and refunds with Bloom's records by ID and amount, and reports
/// the drift. Nothing is corrected. Then purges <c>provider_events</c> older than 90 days (the shared purger's rules are fixed, so payment runs its own batch delete).
/// </summary>
public sealed class NightlyJob(IServiceScopeFactory scopes, TimeProvider clock, PaymentMetrics metrics, ILogger<NightlyJob> logger) : BackgroundService
{
    private const int PurgeBatch = 1000;

    /// <summary>The next 02:00 UTC strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(2);
        return today > now ? today : today.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        for (var next = NextRun(clock.GetUtcNow()); ; next = NextRun(next))
        {
            var wait = next - clock.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, ct);
            try
            {
                await RunTonightAsync(DateOnly.FromDateTime(next.UtcDateTime).AddDays(-1), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Nightly Stripe comparison failed");
            }
            try
            {
                await PurgeEventsAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Purge of provider events failed");
            }
        }
    }

    /// <summary>D26: the comparison of <paramref name="day"/> runs only on the replica that wins the night's claim. False when another replica already has it.</summary>
    internal async Task<bool> RunTonightAsync(DateOnly day, CancellationToken ct)
    {
        if (!await ClaimAsync(ct)) return false;
        await RunAsync(day, ct);
        return true;
    }

    /// <summary>D26: true for the one replica that moves <c>job_runs.last_run_date</c> to today's UTC date (the database's), so the comparison runs once per night.</summary>
    public async Task<bool> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        return await db.Database.ExecuteSqlAsync($"""
            UPDATE job_runs SET last_run_date = (now() AT TIME ZONE 'UTC')::date
            WHERE name = 'nightly_compare' AND last_run_date < (now() AT TIME ZONE 'UTC')::date
            """, ct) == 1;
    }

    /// <summary>
    /// Compares <paramref name="day"/> (UTC). Stripe side: PaymentIntents and refunds created that day with <c>status = succeeded</c>, looked up in Bloom by ID with no date limit. Bloom side: payments
    /// by <c>succeeded_at</c>, refunds by <c>updated_at</c> of the <c>succeeded</c> row; one missing from Stripe's list is fetched by ID before it counts, so a day boundary isn't drift. Returns the
    /// mismatches: <c>stripe_only</c>, <c>bloom_only</c> or <c>amount</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync(DateOnly day, CancellationToken ct)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IPaymentProvider>();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        var mismatches = new List<string>();

        var intents = (await provider.ListPaymentIntentsAsync(from, to, ct)).Where(i => i.Status == "succeeded").ToList();
        var intentIds = intents.Select(i => i.Id).ToArray();
        var known = await db.Payments.AsNoTracking().Where(p => intentIds.Contains(p.StripePaymentIntentId!)).ToDictionaryAsync(p => p.StripePaymentIntentId!, ct);
        foreach (var intent in intents)
        {
            if (!known.TryGetValue(intent.Id, out var payment)) mismatches.Add($"stripe_only payment {intent.Id}");
            else if (payment.AmountCents != intent.Amount) mismatches.Add($"amount payment {intent.Id} stripe={intent.Amount} bloom={payment.AmountCents}");
        }
        var bloomPayments = await db.Payments.AsNoTracking().Where(p => p.Status == PaymentStatuses.Succeeded && p.SucceededAt >= from && p.SucceededAt < to && !intentIds.Contains(p.StripePaymentIntentId!))
            .OrderBy(p => p.SucceededAt).ToListAsync(ct);
        foreach (var payment in bloomPayments)
            if (!await SessionMatchesAsync(provider, payment, ct)) mismatches.Add($"bloom_only payment {payment.StripePaymentIntentId}");

        var refunds = (await provider.ListRefundsAsync(from, to, ct)).Where(r => r.Status == "succeeded").ToList();
        var refundIds = refunds.Select(r => r.Id).ToArray();
        var knownRefunds = await db.Refunds.AsNoTracking().Where(r => r.Status == RefundStatuses.Succeeded && refundIds.Contains(r.StripeRefundId!)).ToDictionaryAsync(r => r.StripeRefundId!, ct);
        foreach (var refund in refunds)
        {
            if (!knownRefunds.TryGetValue(refund.Id, out var row)) mismatches.Add($"stripe_only refund {refund.Id}");
            else if (row.AmountCents != refund.Amount) mismatches.Add($"amount refund {refund.Id} stripe={refund.Amount} bloom={row.AmountCents}");
        }
        var bloomRefunds = await db.Refunds.AsNoTracking().Where(r => r.Status == RefundStatuses.Succeeded && r.UpdatedAt >= from && r.UpdatedAt < to && !refundIds.Contains(r.StripeRefundId!))
            .OrderBy(r => r.UpdatedAt).ToListAsync(ct);
        foreach (var refund in bloomRefunds)
            if (!await RefundMatchesAsync(provider, refund, ct)) mismatches.Add($"bloom_only refund {refund.Id}");

        metrics.SetMismatches(mismatches.Count);
        if (mismatches.Count > 0)
            logger.LogError("Nightly reconciliation of {Day} found {Count} mismatches:\n{Mismatches}", day, mismatches.Count, string.Join('\n', mismatches));
        return mismatches;
    }

    // A failed fetch, or a payment without a session id (succeeded by an early webhook before the claimer stored it), counts as a mismatch: it is reported, never corrected.
    private async Task<bool> SessionMatchesAsync(IPaymentProvider provider, PaymentRow payment, CancellationToken ct)
    {
        if (payment.StripeSessionId is null) return false;
        try
        {
            var s = await provider.GetSessionAsync(payment.StripeSessionId, ct);
            return s is { Status: "complete", PaymentStatus: "paid" } && s.PaymentIntentId == payment.StripePaymentIntentId && s.AmountTotal == payment.AmountCents;
        }
        catch (ProviderException e)
        {
            logger.LogWarning(e, "Stripe lookup of the session of payment {PaymentId} failed; counted as a mismatch", payment.Id);
            return false;
        }
    }

    private async Task<bool> RefundMatchesAsync(IPaymentProvider provider, RefundRow refund, CancellationToken ct)
    {
        try
        {
            var r = await provider.GetRefundAsync(refund.StripeRefundId!, ct);
            return r.Status == "succeeded" && r.Amount == refund.AmountCents;
        }
        catch (ProviderException e)
        {
            logger.LogWarning(e, "Stripe lookup of refund {RefundId} failed; counted as a mismatch", refund.Id);
            return false;
        }
    }

    /// <summary>Deletes <c>provider_events</c> received more than 90 days ago, 1,000 rows per statement, until a statement deletes fewer.</summary>
    public async Task PurgeEventsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        while (await db.Database.ExecuteSqlAsync($"""
            DELETE FROM provider_events WHERE event_id IN (SELECT event_id FROM provider_events WHERE received_at < now() - interval '90 days' LIMIT {PurgeBatch})
            """, ct) >= PurgeBatch) { }
    }
}
