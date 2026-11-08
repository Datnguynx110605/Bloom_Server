using Bloom.Payment.Consumers;
using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Tests;

/// <summary>04-payment §6.4, §7, §8; spec §7 "Cancellations", owner item 4: the cancel command and the executor's session expiry. P-10, PR-01.</summary>
public class CancelTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Expire(string sessionId) => $"v1/checkout/sessions/{sessionId}/expire";

    private static Task Send(TestPayment t, Guid orderId) =>
        Consume.Async<CancelPaymentConsumer>(t, PaymentMessages.CancelPayment, new CancelPaymentV1(orderId));

    private static Task<IReadOnlyList<PaymentSucceededV1>> Succeeded(TestPayment t) => Outbox.PayloadsAsync<PaymentSucceededV1>(t, PaymentMessages.Succeeded);

    [Fact]
    public async Task Cancel_command_expires_the_session()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, sessionId) = await Seed.CreateAsync(t);

        await Send(t, orderId);

        var requested = await t.PaymentByOrderAsync(orderId);
        Assert.Equal((PaymentStatuses.Open, true), (requested.Status, requested.CancelRequested));
        Assert.Equal(0, t.Stripe.Count(Expire(sessionId)));

        await t.RunExecutorAsync();

        Assert.Equal(1, t.Stripe.Count(Expire(sessionId)));
        Assert.Equal(PaymentStatuses.Canceled, (await t.PaymentByOrderAsync(orderId)).Status);
        Assert.Equal("expired", t.Stripe.Session(sessionId).Status);
        Assert.Empty(await Succeeded(t));
        Assert.Empty(t.Stripe.Unserved);
    }

    [Fact]
    public async Task P10_cancel_after_payment_stays_succeeded()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t);
        var calls = t.Stripe.Requests.Count;

        await Send(t, orderId);
        await t.RunExecutorAsync();

        var payment = await t.PaymentByOrderAsync(orderId);
        Assert.Equal((PaymentStatuses.Succeeded, false), (payment.Status, payment.CancelRequested));
        Assert.Equal(calls, t.Stripe.Requests.Count);
        Assert.Single(await Succeeded(t));
    }

    [Fact]
    public async Task Paid_in_the_same_second_as_the_cancel_is_a_late_success()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, response, sessionId) = await Seed.CreateAsync(t);
        var paymentId = Guid.Parse(response.PaymentId);
        await Send(t, orderId);
        var piId = t.Stripe.Pay(sessionId); // the customer pays before the executor's expire call

        await t.RunExecutorAsync();

        var payment = await t.PaymentByOrderAsync(orderId);
        Assert.Equal((PaymentStatuses.Succeeded, piId), (payment.Status, payment.StripePaymentIntentId));
        var sent = Assert.Single(await Succeeded(t));
        Assert.Equal((orderId, paymentId, 1000L), (sent.OrderId, sent.PaymentId, sent.AmountCents));
        Assert.Equal(1, metrics.Sum("payments_succeeded_total", ("late", "true")));
        Assert.Equal(1, t.Stripe.Count(Expire(sessionId)));

        // The completed webhook arrives later: nothing more happens, and the executor leaves the payment alone.
        var body = StripeEvents.Json("checkout.session.completed", StripeEvents.CompletedSession(paymentId, orderId, sessionId, piId), t.Clock.GetUtcNow());
        using var posted = await StripeEvents.PostAsync(t, body);
        posted.EnsureSuccessStatusCode();
        await t.RunExecutorAsync();

        Assert.Single(await Succeeded(t));
        Assert.Equal(1, t.Stripe.Count(Expire(sessionId)));
        Assert.Equal(1, metrics.Sum("payments_succeeded_total", ("late", "true")));
    }

    [Fact]
    public async Task Session_already_expired_at_stripe_moves_to_expired()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, sessionId) = await Seed.CreateAsync(t);
        await Send(t, orderId);
        t.Stripe.ExpireSession(sessionId);

        await t.RunExecutorAsync();

        Assert.Equal(PaymentStatuses.Expired, (await t.PaymentByOrderAsync(orderId)).Status);
        Assert.Equal(1, t.Stripe.Count(Expire(sessionId)));
        Assert.Equal(1, t.Stripe.Requests.Count(r => r is { Method: "GET" } && r.Path == $"v1/checkout/sessions/{sessionId}")); // the retrieve
        Assert.Empty(await Succeeded(t));
    }

    [Fact]
    public async Task Stripe_down_retries_every_tick_until_the_session_expires()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, sessionId) = await Seed.CreateAsync(t);
        await Send(t, orderId);
        t.Stripe.Fail(Expire(sessionId), 503, times: 2);

        await t.RunExecutorAsync();
        Assert.Equal((PaymentStatuses.Open, 1), ((await t.PaymentByOrderAsync(orderId)).Status, t.Stripe.Count(Expire(sessionId))));
        await t.RunExecutorAsync();
        Assert.Equal((PaymentStatuses.Open, 2), ((await t.PaymentByOrderAsync(orderId)).Status, t.Stripe.Count(Expire(sessionId))));
        await t.RunExecutorAsync();
        Assert.Equal((PaymentStatuses.Canceled, 3), ((await t.PaymentByOrderAsync(orderId)).Status, t.Stripe.Count(Expire(sessionId))));

        // Past the session's own expiry the executor no longer picks the payment (the reconciler takes over).
        var (lateOrder, _, lateSession) = await Seed.CreateAsync(t);
        await Send(t, lateOrder);
        t.Clock.Advance(TimeSpan.FromMinutes(48));
        await t.RunExecutorAsync();
        Assert.Equal(0, t.Stripe.Count(Expire(lateSession)));
        Assert.Equal(PaymentStatuses.Open, (await t.PaymentByOrderAsync(lateOrder)).Status);
    }

    [Fact]
    public async Task Cancel_for_creating_unknown_or_final_payments_is_a_no_op()
    {
        await using var t = await TestPayment.StartAsync(containers);
        await Send(t, Guid.NewGuid()); // unknown order

        var (creatingOrder, _, _) = await Seed.CreateAsync(t);
        var (expiredOrder, _, expiredSession) = await Seed.CreateAsync(t);
        var (canceledOrder, _, _) = await Seed.CreateAsync(t);
        await Send(t, canceledOrder);
        await t.RunExecutorAsync();
        await using (var db = t.NewDb())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE payments SET status = 'creating', stripe_session_id = NULL WHERE order_id = {creatingOrder}", Ct);
            await db.Database.ExecuteSqlAsync($"UPDATE payments SET status = 'expired' WHERE order_id = {expiredOrder}", Ct);
        }
        var (paidOrder, _, _) = await Seed.PaidAsync(t);

        foreach (var order in new[] { creatingOrder, expiredOrder, canceledOrder, paidOrder }) await Send(t, order);

        Assert.False((await t.PaymentByOrderAsync(creatingOrder)).CancelRequested);
        Assert.False((await t.PaymentByOrderAsync(expiredOrder)).CancelRequested);
        Assert.Equal(PaymentStatuses.Canceled, (await t.PaymentByOrderAsync(canceledOrder)).Status);
        Assert.False((await t.PaymentByOrderAsync(paidOrder)).CancelRequested);
        await using var check = t.NewDb();
        Assert.Equal(1, await check.Payments.CountAsync(p => p.CancelRequested, Ct)); // only the one that was canceled
        Assert.Equal(0, t.Stripe.Count(Expire(expiredSession)));
    }

    [Fact]
    public async Task Expired_webhook_after_cancel_is_a_no_op()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, response, sessionId) = await Seed.CreateAsync(t);
        await Send(t, orderId);
        await t.RunExecutorAsync();

        var body = StripeEvents.Json("checkout.session.expired", StripeEvents.ExpiredSession(Guid.Parse(response.PaymentId), orderId, sessionId), t.Clock.GetUtcNow());
        using var posted = await StripeEvents.PostAsync(t, body);
        posted.EnsureSuccessStatusCode();

        Assert.Equal(PaymentStatuses.Canceled, (await t.PaymentByOrderAsync(orderId)).Status);
    }
}
