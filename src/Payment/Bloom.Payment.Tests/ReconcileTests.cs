using Bloom.Payment.Consumers;
using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Payment.Tests;

/// <summary>04-payment §6.5, §8, §10; spec §7 "Reconcilers", "provider_events retention": the 5-minute reconciler, the nightly comparison and the event purge. P-09, P-11, PR-06.</summary>
public class ReconcileTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateOnly Day = new(2027, 3, 3); // in the future: FakeTimeProvider only moves forward

    private static readonly DateTimeOffset DayNoon = new(2027, 3, 3, 12, 0, 0, TimeSpan.Zero);

    private static Task Reconcile(TestPayment t) => ActivatorUtilities.CreateInstance<Reconciler>(t.Services).RunOnceAsync(Ct);

    private static NightlyJob Nightly(TestPayment t) => ActivatorUtilities.CreateInstance<NightlyJob>(t.Services);

    private static Task<IReadOnlyList<PaymentSucceededV1>> Succeeded(TestPayment t) => Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded);

    private static Task<IReadOnlyList<RefundCompletedV1>> Completed(TestPayment t) => Outbox.PayloadsAsync<RefundCompletedV1>(t, PaymentMessages.RefundCompleted);

    private static Task<IReadOnlyList<RefundFailedV1>> Failed(TestPayment t) => Outbox.PayloadsAsync<RefundFailedV1>(t, PaymentMessages.RefundFailed);

    private static Task<HttpResponseMessage> PostCompleted(TestPayment t, Guid paymentId, Guid orderId, string sessionId, string piId) =>
        StripeEvents.PostAsync(t, StripeEvents.Json("checkout.session.completed", StripeEvents.CompletedSession(paymentId, orderId, sessionId, piId), t.Clock.GetUtcNow()));

    private static TimeSpan StuckPayment => TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1);

    private static TimeSpan StuckRefund => TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);

    // A paid payment with one succeeded refund at Stripe and in Bloom, created at the fixed clock.
    private static async Task<(Guid PaymentId, string PiId, Guid RefundId)> PaidWithRefundAsync(TestPayment t)
    {
        var (orderId, paymentId, piId) = await Seed.PaidAsync(t, 1000);
        var refundId = Guid.NewGuid();
        await Consume.Async<RefundPaymentConsumer>(t, PaymentMessages.RefundPayment, new RefundPaymentV1(orderId, refundId, 400, "return", null));
        await t.RunExecutorAsync();
        Assert.Equal(RefundStatuses.Succeeded, (await t.RefundAsync(refundId)).Status);
        return (paymentId, piId, refundId);
    }

    [Fact]
    public async Task P09_missed_completed_webhook_is_fixed_by_the_reconciler()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, response, sessionId) = await Seed.CreateAsync(t);
        var paymentId = Guid.Parse(response.PaymentId);
        var piId = t.Stripe.Pay(sessionId); // no webhook arrives
        t.Clock.Advance(StuckPayment);

        await Reconcile(t);

        var payment = await t.PaymentByOrderAsync(orderId);
        Assert.Equal((PaymentStatuses.Succeeded, piId), (payment.Status, payment.StripePaymentIntentId));
        var sent = Assert.Single(await Succeeded(t));
        Assert.Equal((orderId, paymentId, 1000L), (sent.OrderId, sent.PaymentId, sent.AmountCents));
        Assert.Equal(1, metrics.Sum("reconcile_fixes_total", ("kind", "payment_succeeded")));

        using var posted = await PostCompleted(t, paymentId, orderId, sessionId, piId); // the late webhook finds nothing to do
        posted.EnsureSuccessStatusCode();
        Assert.Single(await Succeeded(t));
        Assert.Equal(1, metrics.Sum("reconcile_fixes_total"));
    }

    [Fact]
    public async Task Young_open_payments_are_left_alone()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, sessionId) = await Seed.CreateAsync(t);
        t.Stripe.Pay(sessionId);
        t.Clock.Advance(TimeSpan.FromMinutes(14));

        await Reconcile(t);

        Assert.Equal(PaymentStatuses.Open, (await t.PaymentByOrderAsync(orderId)).Status);
        Assert.Equal(0, t.Stripe.Requests.Count(r => r.Method == "GET"));
    }

    [Fact]
    public async Task Expired_session_is_marked_expired()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, _, sessionId) = await Seed.CreateAsync(t);
        t.Stripe.ExpireSession(sessionId);
        t.Clock.Advance(StuckPayment);

        await Reconcile(t);

        Assert.Equal(PaymentStatuses.Expired, (await t.PaymentByOrderAsync(orderId)).Status);
        Assert.Equal(1, metrics.Sum("reconcile_fixes_total", ("kind", "payment_expired")));
        Assert.Empty(await Succeeded(t));
    }

    [Fact]
    public async Task Stuck_submitted_refund_is_settled()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, paymentId, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "pending";
        var ok = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await Consume.Async<RefundPaymentConsumer>(t, PaymentMessages.RefundPayment, new RefundPaymentV1(orderId, ok, 300, "return", null));
        await Consume.Async<RefundPaymentConsumer>(t, PaymentMessages.RefundPayment, new RefundPaymentV1(orderId, bad, 200, "return", null));
        await t.RunExecutorAsync();
        var okRow = await t.RefundAsync(ok);
        var badRow = await t.RefundAsync(bad);
        Assert.Equal((RefundStatuses.Submitted, RefundStatuses.Submitted), (okRow.Status, badRow.Status));
        t.Stripe.SetRefund(okRow.StripeRefundId!, "succeeded");
        t.Stripe.SetRefund(badRow.StripeRefundId!, "failed", "expired_or_canceled_card");

        t.Clock.Advance(TimeSpan.FromMinutes(10)); // not yet stuck
        await Reconcile(t);
        Assert.Equal(RefundStatuses.Submitted, (await t.RefundAsync(ok)).Status);

        t.Clock.Advance(TimeSpan.FromSeconds(1));
        await Reconcile(t);

        Assert.Equal(RefundStatuses.Succeeded, (await t.RefundAsync(ok)).Status);
        var failed = await t.RefundAsync(bad);
        Assert.Equal((RefundStatuses.Failed, "expired_or_canceled_card"), (failed.Status, failed.FailureReason));
        await using var db = t.NewDb();
        Assert.Equal(300, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct)).RefundedCents);
        Assert.Equal(new RefundCompletedV1(orderId, ok, 300, null), Assert.Single(await Completed(t)));
        Assert.Equal(new RefundFailedV1(orderId, bad, "expired_or_canceled_card"), Assert.Single(await Failed(t)));
        Assert.Equal(1, metrics.Sum("reconcile_fixes_total", ("kind", "refund_succeeded")));
        Assert.Equal(1, metrics.Sum("reconcile_fixes_total", ("kind", "refund_failed")));
    }

    [Fact]
    public async Task D24_reconciler_settles_submitted_unknown_refund()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t, 1000);
        t.Stripe.NextRefundStatus = "some_new_status";
        var refundId = Guid.NewGuid();
        await Consume.Async<RefundPaymentConsumer>(t, PaymentMessages.RefundPayment, new RefundPaymentV1(orderId, refundId, 400, "return", null));
        await t.RunExecutorAsync();
        var row = await t.RefundAsync(refundId);
        Assert.Equal(RefundStatuses.Submitted, row.Status);
        t.Stripe.SetRefund(row.StripeRefundId!, "succeeded");
        t.Clock.Advance(StuckRefund);

        await Reconcile(t);

        Assert.Equal(RefundStatuses.Succeeded, (await t.RefundAsync(refundId)).Status);
        Assert.Equal(new RefundCompletedV1(orderId, refundId, 400, null), Assert.Single(await Completed(t)));
    }

    [Fact]
    public async Task Webhook_and_reconciler_apply_once()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var paid = new List<Guid>();
        for (var round = 0; round < 10; round++)
        {
            var (orderId, response, sessionId) = await Seed.CreateAsync(t);
            var paymentId = Guid.Parse(response.PaymentId);
            var piId = t.Stripe.Pay(sessionId);
            t.Clock.Advance(StuckPayment);

            var reconcile = Reconcile(t);
            using var posted = await PostCompleted(t, paymentId, orderId, sessionId, piId);
            await reconcile;

            posted.EnsureSuccessStatusCode();
            paid.Add(paymentId);
        }

        var sent = await Succeeded(t);
        Assert.Equal(paid.Order(), sent.Select(s => s.PaymentId).Order()); // exactly one event per payment
        Assert.InRange(metrics.Sum("reconcile_fixes_total"), 0, 10);
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task A_stripe_error_skips_one_record()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (firstOrder, _, first) = await Seed.CreateAsync(t);
        var (secondOrder, _, second) = await Seed.CreateAsync(t);
        t.Stripe.Pay(first);
        t.Stripe.Pay(second);
        t.Clock.Advance(StuckPayment);
        t.Stripe.Fail("v1/checkout/sessions/", 500);

        await Reconcile(t);

        var statuses = new[] { (await t.PaymentByOrderAsync(firstOrder)).Status, (await t.PaymentByOrderAsync(secondOrder)).Status };
        Assert.Equal([PaymentStatuses.Open, PaymentStatuses.Succeeded], statuses.Order());
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Warning]", StringComparison.Ordinal));
        Assert.Equal(0, t.ErrorLogs);
        Assert.Single(await Succeeded(t));

        await Reconcile(t); // the skipped record is retried on the next pass
        Assert.Equal(2, (await Succeeded(t)).Count);
    }

    [Fact]
    public async Task Disputes_open_counts_open_disputes()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (_, paymentId, _) = await Seed.PaidAsync(t);
        await using (var db = t.NewDb())
        {
            var now = t.Clock.GetUtcNow();
            db.Disputes.AddRange(new[] { "needs_response", "under_review", "won" }.Select((status, i) => new DisputeRow
            {
                Id = Guid.NewGuid(), PaymentId = paymentId, StripeDisputeId = $"dp_{i}", Status = status, AmountCents = 100, Reason = "general", OpenedAt = now, UpdatedAt = now,
            }));
            await db.SaveChangesAsync(Ct);
        }

        await Reconcile(t);

        Assert.Equal(2, metrics.Latest("disputes_open"));
    }

    [Fact]
    public async Task P11_dashboard_only_refund_is_a_mismatch()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        t.Clock.SetUtcNow(DayNoon);
        var (_, piId, _) = await PaidWithRefundAsync(t);
        var dashboard = t.Stripe.AddDashboardRefund(piId, 100, DayNoon.AddHours(-2));

        var mismatches = await Nightly(t).RunAsync(Day, Ct);

        Assert.Equal([$"stripe_only refund {dashboard}"], mismatches);
        Assert.Equal(1, metrics.Latest("reconcile_mismatches"));
        var error = Assert.Single(t.Logs.Entries, e => e.StartsWith("[Error]", StringComparison.Ordinal));
        Assert.Contains($"stripe_only refund {dashboard}", error);
    }

    [Fact]
    public async Task Matching_day_has_no_mismatch()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        t.Clock.SetUtcNow(DayNoon);
        await PaidWithRefundAsync(t);

        var mismatches = await Nightly(t).RunAsync(Day, Ct);

        Assert.Empty(mismatches);
        Assert.Equal(0, metrics.Latest("reconcile_mismatches"));
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task Day_boundary_is_not_drift()
    {
        await using var t = await TestPayment.StartAsync(containers);
        t.Clock.SetUtcNow(new DateTimeOffset(2027, 3, 2, 23, 59, 50, TimeSpan.Zero));
        var (orderId, response, sessionId) = await Seed.CreateAsync(t);
        var piId = t.Stripe.Pay(sessionId, new DateTimeOffset(2027, 3, 2, 23, 59, 59, TimeSpan.Zero));
        t.Clock.SetUtcNow(new DateTimeOffset(2027, 3, 3, 0, 0, 1, TimeSpan.Zero)); // Bloom learns of it after midnight
        using var posted = await PostCompleted(t, Guid.Parse(response.PaymentId), orderId, sessionId, piId);
        posted.EnsureSuccessStatusCode();

        Assert.Empty(await Nightly(t).RunAsync(Day, Ct));
        Assert.Empty(await Nightly(t).RunAsync(Day.AddDays(-1), Ct));
    }

    [Fact]
    public async Task Bloom_only_and_amount_mismatches_are_reported()
    {
        await using var t = await TestPayment.StartAsync(containers);
        t.Clock.SetUtcNow(DayNoon);
        var (paymentId, piId, _) = await PaidWithRefundAsync(t);
        var ghost = Guid.NewGuid();
        await using (var db = t.NewDb())
        {
            var now = t.Clock.GetUtcNow();
            db.Refunds.Add(new RefundRow
            {
                Id = ghost, PaymentId = paymentId, OrderId = Guid.NewGuid(), AmountCents = 100, Reason = "return", Status = RefundStatuses.Succeeded,
                StripeRefundId = "re_ghost", CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync(Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE payments SET amount_cents = 900 WHERE id = {paymentId}", Ct);
        }

        var mismatches = await Nightly(t).RunAsync(Day, Ct);

        Assert.Equal([$"amount payment {piId} stripe=1000 bloom=900", $"bloom_only refund {ghost}"], mismatches);
        await using var after = t.NewDb();
        Assert.Equal(900, (await after.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct)).AmountCents); // reported, never corrected
    }

    [Fact]
    public async Task Succeeded_payment_without_a_session_id_is_a_mismatch()
    {
        await using var t = await TestPayment.StartAsync(containers);
        t.Clock.SetUtcNow(DayNoon);
        var (orderId, response, _) = await Seed.CreateAsync(t);
        await t.SqlAsync($"UPDATE payments SET status = 'succeeded', succeeded_at = {DayNoon}, stripe_payment_intent_id = 'pi_early', stripe_session_id = NULL WHERE order_id = {orderId}"); // an early webhook won the race

        var mismatches = await Nightly(t).RunAsync(Day, Ct);

        Assert.Equal(["bloom_only payment pi_early"], mismatches);
    }

    [Fact]
    public async Task Lists_are_read_across_pages()
    {
        await using var t = await TestPayment.StartAsync(containers);
        t.Clock.SetUtcNow(DayNoon);
        t.Stripe.PageSize = 2;
        for (var i = 0; i < 5; i++) await Seed.PaidAsync(t);

        Assert.Empty(await Nightly(t).RunAsync(Day, Ct));
        Assert.Equal(3, t.Stripe.Count("v1/payment_intents"));
    }

    [Fact]
    public void Next_run_is_the_next_0200_utc()
    {
        var at = (int h, int m) => new DateTimeOffset(2026, 11, 3, h, m, 0, TimeSpan.Zero);
        Assert.Equal(at(2, 0), NightlyJob.NextRun(at(1, 59)));
        Assert.Equal(at(2, 0).AddDays(1), NightlyJob.NextRun(at(2, 0)));
        Assert.Equal(at(2, 0).AddDays(1), NightlyJob.NextRun(at(23, 0)));
    }

    [Fact]
    public async Task D26_two_workers_same_night_one_run()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var today = await t.NewDb().Database.SqlQuery<DateTime>($"SELECT (now() AT TIME ZONE 'UTC')::date AS \"Value\"").SingleAsync(Ct);

        var claims = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Nightly(t).ClaimAsync(Ct)));

        Assert.Equal(1, claims.Count(c => c));
        Assert.False(await Nightly(t).ClaimAsync(Ct)); // a third replica, later the same night
        await using var db = t.NewDb();
        Assert.Equal(DateOnly.FromDateTime(today), (await db.JobRuns.AsNoTracking().SingleAsync(j => j.Name == "nightly_compare", Ct)).LastRunDate);

        await t.SqlAsync($"UPDATE job_runs SET last_run_date = last_run_date - 1"); // the next night
        Assert.True(await Nightly(t).ClaimAsync(Ct));
    }

    [Fact]
    public async Task Provider_events_older_than_90_days_are_purged()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var now = t.Clock.GetUtcNow();
        var old = now.AddDays(-91);
        var recent = now.AddDays(-89);
        await using (var db = t.NewDb())
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO provider_events (event_id, type, payload, received_at)
                SELECT 'old_' || n, 'charge.dispute.created', to_jsonb(n), {old} FROM generate_series(1, 2500) n
                """, Ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO provider_events (event_id, type, payload, received_at)
                SELECT 'new_' || n, 'charge.dispute.created', to_jsonb(n), {recent} FROM generate_series(1, 3) n
                """, Ct);
        }

        await Nightly(t).PurgeEventsAsync(Ct);

        await using var after = t.NewDb();
        Assert.Equal(["new_1", "new_2", "new_3"], await after.ProviderEvents.Select(e => e.EventId).OrderBy(e => e).ToListAsync(Ct));
    }
}
