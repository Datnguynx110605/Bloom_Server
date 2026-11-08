using Bloom.Payment.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Tests;

/// <summary>Payments made through the real <c>CreatePayment</c> (and, for a paid one, the signed webhook), for the tests of every later task.</summary>
public static class Seed
{
    /// <summary>A valid request; with no <paramref name="lines"/> one line <c>Device × 1</c> carries the full amount.</summary>
    public static CreatePaymentRequest Request(Guid orderId, long amount, params (string Name, long Cents)[] lines)
    {
        if (lines.Length == 0) lines = [("Device × 1", amount)];
        var request = new CreatePaymentRequest
        {
            OrderId = orderId.ToString("D"),
            OrderNumber = "BLM-" + orderId.ToString("N")[..8].ToUpperInvariant(),
            AccountId = Guid.NewGuid().ToString("D"),
            AmountCents = amount,
        };
        request.DisplayLines.AddRange(lines.Select(l => new DisplayLine { Name = l.Name, AmountCents = l.Cents }));
        return request;
    }

    /// <summary>Creates an open payment (a new order unless <paramref name="orderId"/> is given); <c>SessionId</c> is the fake's session.</summary>
    public static async Task<(Guid OrderId, CreatePaymentResponse Response, string SessionId)> CreateAsync(TestPayment t, long amount = 1000, Guid? orderId = null)
    {
        var order = orderId ?? Guid.NewGuid();
        var response = await t.Internal.CreatePaymentAsync(Request(order, amount), cancellationToken: TestContext.Current.CancellationToken);
        await using var db = t.NewDb();
        var sessionId = await db.Payments.AsNoTracking().Where(p => p.OrderId == order).Select(p => p.StripeSessionId).SingleAsync(TestContext.Current.CancellationToken);
        return (order, response, sessionId!);
    }

    /// <summary>Creates a payment, the customer pays in the fake, and the signed <c>checkout.session.completed</c> is posted: the payment is <c>succeeded</c>.</summary>
    public static async Task<(Guid OrderId, Guid PaymentId, string PiId)> PaidAsync(TestPayment t, long amount = 1000)
    {
        var (orderId, response, sessionId) = await CreateAsync(t, amount);
        var paymentId = Guid.Parse(response.PaymentId);
        var piId = t.Stripe.Pay(sessionId);
        var body = StripeEvents.Json("checkout.session.completed", StripeEvents.CompletedSession(paymentId, orderId, sessionId, piId), t.Clock.GetUtcNow());
        using var posted = await StripeEvents.PostAsync(t, body);
        posted.EnsureSuccessStatusCode();
        return (orderId, paymentId, piId);
    }
}
