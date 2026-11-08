using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bloom.Payment.Tests;

/// <summary>Signed Stripe webhook events for the app's <c>POST /webhooks/stripe</c> (secret <c>whsec_test</c>, the key of <see cref="TestPayment"/>).</summary>
public static class StripeEvents
{
    public const string Secret = "whsec_test";

    public static string Json(string type, object dataObject, DateTimeOffset created, string? id = null) =>
        JsonSerializer.Serialize(new { id = id ?? $"evt_test_{Guid.NewGuid()}", @object = "event", type, created = created.ToUnixTimeSeconds(), data = new { @object = dataObject } });

    /// <summary>The <c>Stripe-Signature</c> header: <c>t=&lt;unix&gt;,v1=&lt;hex HMAC-SHA256 of "t.body"&gt;</c>.</summary>
    public static string Sign(string body, string secret, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds();
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")))}";
    }

    /// <summary>Posts <paramref name="body"/>, signed with the test secret at the test clock unless <paramref name="header"/> is given.</summary>
    public static Task<HttpResponseMessage> PostAsync(TestPayment t, string body, string? header = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", header ?? Sign(body, Secret, t.Clock.GetUtcNow()));
        return t.Http.SendAsync(request);
    }

    public static object CompletedSession(Guid paymentId, Guid orderId, string sessionId, string piId) =>
        new { id = sessionId, @object = "checkout.session", status = "complete", payment_status = "paid", payment_intent = piId, metadata = Metadata(paymentId, orderId) };

    public static object ExpiredSession(Guid paymentId, Guid orderId, string sessionId) =>
        new { id = sessionId, @object = "checkout.session", status = "expired", payment_status = "unpaid", payment_intent = (string?)null, metadata = Metadata(paymentId, orderId) };

    /// <summary>A <c>payment_intent.payment_failed</c> object; the intent carries only <c>payment_id</c> (the payment is found by it).</summary>
    public static object FailedIntent(Guid paymentId, string? declineCode, string? code) =>
        new
        {
            id = $"pi_test_{Guid.NewGuid():N}", @object = "payment_intent", status = "requires_payment_method",
            metadata = new { payment_id = paymentId.ToString("D") }, last_payment_error = new { decline_code = declineCode, code },
        };

    public static object Refund(string id, string status, Guid? refundId, string? failureReason = null) =>
        new
        {
            id, @object = "refund", status, failure_reason = failureReason,
            metadata = refundId is { } r ? new Dictionary<string, string> { ["refund_id"] = r.ToString("D") } : [],
        };

    public static object Dispute(string id, string? piId, string status, long amount, string reason) =>
        new { id, @object = "dispute", payment_intent = piId, status, amount, reason };

    private static object Metadata(Guid paymentId, Guid orderId) => new { order_id = orderId.ToString("D"), payment_id = paymentId.ToString("D") };
}
