using System.Text;
using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Payment.Tests;

/// <summary>04-payment §6.2, §8; spec §7: <c>POST /webhooks/stripe</c> (PR-03), payment transitions and disputes. P-03, P-04, P-05.</summary>
public class WebhookTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<int> EventCountAsync(TestPayment t)
    {
        await using var db = t.NewDb();
        return await db.ProviderEvents.CountAsync(Ct);
    }

    private static Task<HttpResponseMessage> Post(TestPayment t, string type, object obj, string? id = null) =>
        StripeEvents.PostAsync(t, StripeEvents.Json(type, obj, t.Clock.GetUtcNow(), id));

    /// <summary>A payment in the given status, with the event that completes it.</summary>
    private static async Task<(Guid OrderId, Guid PaymentId, string Session, string Pi)> OpenAsync(TestPayment t, long amount = 1000)
    {
        var (orderId, response, session) = await Seed.CreateAsync(t, amount);
        return (orderId, Guid.Parse(response.PaymentId), session, t.Stripe.Pay(session));
    }

    private static Task<HttpResponseMessage> Complete(TestPayment t, (Guid OrderId, Guid PaymentId, string Session, string Pi) p, string? eventId = null) =>
        Post(t, "checkout.session.completed", StripeEvents.CompletedSession(p.PaymentId, p.OrderId, p.Session, p.Pi), eventId);

    private static async Task<HttpResponseMessage> RawAsync(TestPayment t, string body, string? header)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (header is not null) request.Headers.TryAddWithoutValidation("Stripe-Signature", header);
        return await t.Http.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Completed_marks_succeeded_and_publishes()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var p = await OpenAsync(t);
        t.Clock.Advance(TimeSpan.FromSeconds(30));
        var now = t.Clock.GetUtcNow();

        using var response = await Complete(t, p);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var row = await t.PaymentAsync(p.PaymentId);
        Assert.Equal(PaymentStatuses.Succeeded, row.Status);
        Assert.Equal(p.Pi, row.StripePaymentIntentId);
        Assert.Equal(now, row.SucceededAt);
        var sent = Assert.Single(await Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded));
        Assert.Equal(new PaymentSucceededV1(p.OrderId, p.PaymentId, 1000, now), sent);
        Assert.Equal(1, await EventCountAsync(t));
        Assert.Equal(1, metrics.Sum("payments_succeeded_total", ("late", "false")));
        Assert.Equal(1, metrics.Sum("webhooks_total", ("type", "checkout.session.completed"), ("result", "processed")));
        Assert.Equal(1, metrics.Count("webhook_lag_seconds"));
    }

    [Fact]
    public async Task P03_same_event_twice_is_processed_once()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var p = await OpenAsync(t);

        using var first = await Complete(t, p, "evt_same");
        using var second = await Complete(t, p, "evt_same");

        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, second.StatusCode);
        Assert.Single(await Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded));
        Assert.Equal(1, await EventCountAsync(t));
        Assert.Equal(1, metrics.Sum("webhooks_total", ("result", "processed")));
        Assert.Equal(1, metrics.Sum("webhooks_total", ("result", "duplicate")));
    }

    [Fact]
    public async Task Same_event_in_parallel_is_processed_once()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var p = await OpenAsync(t);
        var body = StripeEvents.Json("checkout.session.completed", StripeEvents.CompletedSession(p.PaymentId, p.OrderId, p.Session, p.Pi), t.Clock.GetUtcNow(), "evt_parallel");

        var responses = await Task.WhenAll(StripeEvents.PostAsync(t, body), StripeEvents.PostAsync(t, body));

        Assert.All(responses, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode));
        Assert.Single(await Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded));
        Assert.Equal(1, metrics.Sum("webhooks_total", ("result", "processed")));
        Assert.Equal(1, metrics.Sum("webhooks_total", ("result", "duplicate")));
    }

    [Fact]
    public async Task P04_bad_signature_is_400_and_stores_nothing()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var p = await OpenAsync(t);
        var body = StripeEvents.Json("checkout.session.completed", StripeEvents.CompletedSession(p.PaymentId, p.OrderId, p.Session, p.Pi), t.Clock.GetUtcNow());
        var now = t.Clock.GetUtcNow();

        var cases = new (string Body, string? Header)[]
        {
            (body, StripeEvents.Sign(body, "whsec_other", now)), // wrong secret
            (body, null), // no header
            (body, StripeEvents.Sign(body, StripeEvents.Secret, now.AddMinutes(-6))), // stale t
            (body + " ", StripeEvents.Sign(body, StripeEvents.Secret, now)), // body altered after signing
        };
        foreach (var (sent, header) in cases)
        {
            using var response = await RawAsync(t, sent, header);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, await EventCountAsync(t));
        Assert.Equal(PaymentStatuses.Open, (await t.PaymentAsync(p.PaymentId)).Status);
        Assert.Equal(4, metrics.Sum("webhooks_total", ("type", "unknown"), ("result", "bad_signature")));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]"));
    }

    [Fact]
    public async Task P05_completed_after_canceled_succeeds_and_publishes()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);

        foreach (var setup in new Func<Guid, FormattableString>[]
        {
            id => $"UPDATE payments SET status = 'canceled' WHERE id = {id}",
            id => $"UPDATE payments SET status = 'expired' WHERE id = {id}",
            id => $"UPDATE payments SET cancel_requested = true WHERE id = {id}",
        })
        {
            var p = await OpenAsync(t);
            await t.SqlAsync(setup(p.PaymentId));
            using var response = await Complete(t, p);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(PaymentStatuses.Succeeded, (await t.PaymentAsync(p.PaymentId)).Status);
        }

        Assert.Equal(3, (await Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded)).Count);
        Assert.Equal(3, metrics.Sum("payments_succeeded_total", ("late", "true")));
        Assert.Equal(0, metrics.Sum("payments_succeeded_total", ("late", "false")));
    }

    [Fact]
    public async Task Completed_before_tx2_keeps_succeeded_and_stores_the_url()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var orderId = Guid.NewGuid();
        var fired = false;
        t.Stripe.Before = async path =>
        {
            if (path != "v1/checkout/sessions" || fired) return;
            fired = true;
            await using var db = t.NewDb();
            var id = await db.Payments.Where(p => p.OrderId == orderId).Select(p => p.Id).SingleAsync(Ct);
            using var response = await Post(t, "checkout.session.completed", StripeEvents.CompletedSession(id, orderId, "cs_early", "pi_early"));
            response.EnsureSuccessStatusCode();
        };

        var created = await t.Internal.CreatePaymentAsync(Seed.Request(orderId, 1000), cancellationToken: Ct);

        var row = await t.PaymentAsync(Guid.Parse(created.PaymentId));
        Assert.Equal(PaymentStatuses.Succeeded, row.Status);
        Assert.Equal("pi_early", row.StripePaymentIntentId);
        Assert.NotEmpty(created.CheckoutUrl);
        Assert.Equal(created.CheckoutUrl, row.CheckoutUrl);
        Assert.Equal("cs_test_1", row.StripeSessionId);
        Assert.Single(await Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded));
    }

    [Fact]
    public async Task Expired_moves_only_open_payments()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var open = await OpenAsync(t);
        var paid = await OpenAsync(t);
        using var completed = await Complete(t, paid);

        using var a = await Post(t, "checkout.session.expired", StripeEvents.ExpiredSession(open.PaymentId, open.OrderId, open.Session));
        using var b = await Post(t, "checkout.session.expired", StripeEvents.ExpiredSession(paid.PaymentId, paid.OrderId, paid.Session));

        Assert.Equal(PaymentStatuses.Expired, (await t.PaymentAsync(open.PaymentId)).Status);
        Assert.Equal(PaymentStatuses.Succeeded, (await t.PaymentAsync(paid.PaymentId)).Status);
        Assert.Single(await Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded)); // no event for the expiry
        Assert.Empty(await Outbox.PayloadsAsync<PaymentFailedV1>(t, PaymentMessages.Failed));
    }

    [Fact]
    public async Task Card_declined_publishes_failed_per_attempt()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var p = await OpenAsync(t);

        foreach (var (decline, code) in new (string?, string?)[] { ("card_declined", "card_declined_code"), (null, "expired_card"), (null, null) })
        {
            using var response = await Post(t, "payment_intent.payment_failed", StripeEvents.FailedIntent(p.PaymentId, decline, code));
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        var failed = await Outbox.PayloadsAsync<PaymentFailedV1>(t, PaymentMessages.Failed);
        Assert.Equal(["card_declined", "expired_card", "unknown"], failed.Select(f => f.FailureCode));
        Assert.All(failed, f => Assert.Equal(new(p.OrderId, p.PaymentId), (f.OrderId, f.PaymentId)));
        Assert.Equal(PaymentStatuses.Open, (await t.PaymentAsync(p.PaymentId)).Status);
    }

    [Fact]
    public async Task Unknown_records_and_types_are_stored_only()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var p = await OpenAsync(t);

        using var a = await Post(t, "checkout.session.completed", StripeEvents.CompletedSession(Guid.NewGuid(), p.OrderId, p.Session, p.Pi));
        using var b = await Post(t, "customer.created", new { id = "cus_1", @object = "customer" });
        using var c = await Post(t, "payment_intent.payment_failed", StripeEvents.FailedIntent(Guid.NewGuid(), "card_declined", null));

        Assert.All(new[] { a, b, c }, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(3, await EventCountAsync(t));
        Assert.Equal(PaymentStatuses.Open, (await t.PaymentAsync(p.PaymentId)).Status);
        Assert.Empty(await Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded));
        Assert.Empty(await Outbox.PayloadsAsync<PaymentFailedV1>(t, PaymentMessages.Failed));
        Assert.Equal(1, metrics.Sum("webhooks_total", ("type", "customer.created"), ("result", "processed")));
    }

    [Fact]
    public async Task Disputes_are_upserted_and_alerted()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var p = await OpenAsync(t);
        using var paid = await Complete(t, p);
        await OpenAsync(t); // an open payment has no intent, which a dispute without one must not match

        using var created = await Post(t, "charge.dispute.created", StripeEvents.Dispute("dp_1", p.Pi, "needs_response", 1000, "fraudulent"));
        await using (var db = t.NewDb())
        {
            var row = await db.Disputes.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((p.PaymentId, "dp_1", "needs_response", 1000L, "fraudulent"), (row.PaymentId, row.StripeDisputeId, row.Status, row.AmountCents, row.Reason));
        }
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains("dp_1") && e.Contains(p.PaymentId.ToString()) && e.Contains("needs_response"));

        t.Clock.Advance(TimeSpan.FromHours(1));
        using var updated = await Post(t, "charge.dispute.closed", StripeEvents.Dispute("dp_1", p.Pi, "won", 1000, "fraudulent"));
        using var unknown = await Post(t, "charge.dispute.created", StripeEvents.Dispute("dp_2", "pi_nobody", "needs_response", 500, "general"));
        using var noIntent = await Post(t, "charge.dispute.created", StripeEvents.Dispute("dp_3", null, "needs_response", 500, "general"));

        await using var db2 = t.NewDb();
        var after = await db2.Disputes.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("won", after.Status);
        Assert.True(after.UpdatedAt > after.OpenedAt);
        Assert.Equal(5, await EventCountAsync(t)); // completed, created, closed, the unknown one, the one without an intent
    }

    [Fact]
    public async Task P4_dispute_alert_not_logged_on_rollback()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var p = await OpenAsync(t);
        using var paid = await Complete(t, p);
        // the upsert succeeds and the commit fails, so the row is rolled back
        await t.SqlAsync($"CREATE FUNCTION fail_at_commit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'commit refused'; END $$");
        await t.SqlAsync($"CREATE CONSTRAINT TRIGGER ck_test_never AFTER INSERT ON disputes DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_at_commit()");

        using var failed = await Post(t, "charge.dispute.created", StripeEvents.Dispute("dp_1", p.Pi, "needs_response", 1000, "fraudulent"));

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, failed.StatusCode);
        await using var db = t.NewDb();
        Assert.Empty(await db.Disputes.AsNoTracking().ToListAsync(Ct));
        Assert.DoesNotContain(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains("Stripe dispute"));
    }

    [Fact]
    public async Task D25_late_created_after_closed_keeps_final_status()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var p = await OpenAsync(t);
        using var paid = await Complete(t, p);
        var early = DateTimeOffset.FromUnixTimeSeconds(t.Clock.GetUtcNow().ToUnixTimeSeconds()); // Stripe's created has whole seconds
        var late = early.AddMinutes(10);
        t.Clock.Advance(TimeSpan.FromMinutes(11));

        using var closed = await StripeEvents.PostAsync(t, StripeEvents.Json("charge.dispute.closed", StripeEvents.Dispute("dp_1", p.Pi, "won", 1000, "fraudulent"), late));
        using var stale = await StripeEvents.PostAsync(t, StripeEvents.Json("charge.dispute.created", StripeEvents.Dispute("dp_1", p.Pi, "needs_response", 1000, "fraudulent"), early));

        Assert.Equal(System.Net.HttpStatusCode.OK, stale.StatusCode);
        Assert.DoesNotContain(t.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains("needs_response")); // a stale event must not alert Support
        await using (var db = t.NewDb())
        {
            var row = await db.Disputes.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(("won", late), (row.Status, row.UpdatedAt));
        }
        await ActivatorUtilities.CreateInstance<Reconciler>(t.Services).RunOnceAsync(Ct);
        Assert.Equal(0, metrics.Latest("disputes_open"));
    }

    [Fact]
    public async Task Processing_error_rolls_back_and_is_500()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var p = await OpenAsync(t);
        await t.SqlAsync($"ALTER TABLE payments ADD CONSTRAINT ck_test_never CHECK (status <> 'succeeded') NOT VALID");

        using var failed = await Complete(t, p, "evt_retry");

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await EventCountAsync(t));
        Assert.Equal(PaymentStatuses.Open, (await t.PaymentAsync(p.PaymentId)).Status);
        Assert.Equal(1, metrics.Sum("webhooks_total", ("type", "checkout.session.completed"), ("result", "error")));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]"));

        await t.SqlAsync($"ALTER TABLE payments DROP CONSTRAINT ck_test_never");
        using var again = await Complete(t, p, "evt_retry");

        Assert.Equal(System.Net.HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(PaymentStatuses.Succeeded, (await t.PaymentAsync(p.PaymentId)).Status);
        Assert.Equal(1, metrics.Sum("webhooks_total", ("result", "processed")));
    }

    // P-10: the body is read through a pooled buffer; behaviour at the limit is unchanged.
    [Fact]
    public async Task P10_webhook_body_uses_pooled_buffer()
    {
        await using var t = await TestPayment.StartAsync(containers);
        const int Limit = 256 * 1024;
        var bare = StripeEvents.Json("customer.created", new { pad = "" }, t.Clock.GetUtcNow(), "evt_limit");
        var exact = StripeEvents.Json("customer.created", new { pad = new string('x', Limit - bare.Length) }, t.Clock.GetUtcNow(), "evt_limit");
        Assert.Equal(Limit, System.Text.Encoding.UTF8.GetByteCount(exact));

        using var accepted = await StripeEvents.PostAsync(t, exact);
        using var refused = await StripeEvents.PostAsync(t, exact.Replace("\"pad\":\"", "\"pad\":\"x", StringComparison.Ordinal));

        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, await EventCountAsync(t));
        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
    }

    [Fact]
    public async Task Oversized_or_malformed_bodies()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var now = t.Clock.GetUtcNow();

        using var big = await StripeEvents.PostAsync(t, new string('x', 256 * 1024 + 1));
        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, big.StatusCode);

        var created = now.ToUnixTimeSeconds();
        var malformed = new[]
        {
            "[]", "not json",
            $$"""{"type":"customer.created","created":{{created}}}""", // no id
            $$"""{"id":"evt_1","created":{{created}}}""", // no type
            """{"id":"evt_1","type":"customer.created","created":"soon"}""", // created not an integer
            $$"""{"id":5,"type":"customer.created","created":{{created}}}""", // id not a string
        };
        foreach (var body in malformed)
        {
            using var response = await StripeEvents.PostAsync(t, body);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, await EventCountAsync(t));
        Assert.Equal(malformed.Length + 1, metrics.Sum("webhooks_total", ("type", "unknown"), ("result", "error")));
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]"));
    }
}
