namespace Bloom.Payment.Stripe;

/// <summary>Every call Payment makes to Stripe (HLD-ADR-018). Failures surface as <see cref="ProviderException"/>.</summary>
public interface IPaymentProvider
{
    /// <summary><c>POST v1/checkout/sessions</c> with key <c>bloom:payment.create:&lt;order_id&gt;</c>; gives up after 4 s.</summary>
    Task<StripeSession> CreateSessionAsync(SessionRequest request, CancellationToken ct);
    Task<StripeSession> GetSessionAsync(string sessionId, CancellationToken ct);
    /// <summary><c>POST v1/checkout/sessions/{id}/expire</c>, no idempotency key.</summary>
    Task<StripeSession> ExpireSessionAsync(string sessionId, CancellationToken ct);
    /// <summary><c>POST v1/refunds</c> with key <c>bloom:refund:&lt;refundId&gt;</c>.</summary>
    Task<StripeRefund> CreateRefundAsync(Guid refundId, Guid orderId, string paymentIntentId, long amountCents, CancellationToken ct);
    Task<StripeRefund> GetRefundAsync(string stripeRefundId, CancellationToken ct);
    /// <summary>Refunds created in <c>[from, to)</c>, every page.</summary>
    Task<IReadOnlyList<StripeRefund>> ListRefundsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    /// <summary>PaymentIntents created in <c>[from, to)</c>, every page.</summary>
    Task<IReadOnlyList<StripePaymentIntent>> ListPaymentIntentsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public sealed record SessionRequest(Guid OrderId, Guid PaymentId, string OrderNumber, DateTimeOffset ExpiresAt, IReadOnlyList<(string Name, long AmountCents)> Lines);

public sealed record StripeSession(string Id, string? Url, string Status, string? PaymentStatus, string? PaymentIntentId, long? AmountTotal, DateTimeOffset ExpiresAt);

/// <param name="RefundId">Bloom's refund id, from <c>metadata.refund_id</c>; null for a refund made in Stripe's dashboard.</param>
public sealed record StripeRefund(string Id, string Status, long Amount, string? FailureReason, Guid? RefundId, DateTimeOffset Created);

public sealed record StripePaymentIntent(string Id, string Status, long Amount, DateTimeOffset Created);

/// <summary>A failed Stripe call (plan R10). <see cref="Transient"/> (5xx, 429, timeout, network, unreadable answer) is worth retrying; otherwise <see cref="Reason"/> is Stripe's <c>error.code</c> ?? <c>error.type</c> ?? <c>http_&lt;status&gt;</c>.</summary>
public sealed class ProviderException(bool transient, string reason, string message, Exception? inner = null) : Exception(message, inner)
{
    public const string Unavailable = "provider_unavailable";

    public bool Transient { get; } = transient;
    public string Reason { get; } = reason;
}
