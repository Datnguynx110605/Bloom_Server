using Bloom.Payment.Contracts;

namespace Bloom.Payment.Data;

// Column types, constraints and indexes are in PaymentDbContext; LLD 04-payment §4.

public static class PaymentStatuses
{
    public const string Creating = "creating";
    public const string Open = "open";
    public const string Succeeded = "succeeded";
    public const string Expired = "expired";
    public const string Canceled = "canceled";

    public static readonly string[] All = [Creating, Open, Succeeded, Expired, Canceled];
}

public static class RefundStatuses
{
    public const string Pending = "pending";
    public const string Submitted = "submitted";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public static readonly string[] All = [Pending, Submitted, Succeeded, Failed];
}

public class PaymentRow
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    /// <summary>Shown on Stripe's page.</summary>
    public required string OrderNumber { get; set; }
    public Guid AccountId { get; set; }
    public long AmountCents { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    /// <summary><c>creating</c> only: claim + 60 s.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
    public string? StripeSessionId { get; set; }
    public string? StripePaymentIntentId { get; set; }
    public string? CheckoutUrl { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    public bool CancelRequested { get; set; }
    public long RefundedCents { get; set; }
    public DateTimeOffset? SucceededAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The id is the command's <c>refundId</c> (idempotent).</summary>
public class RefundRow
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public Guid OrderId { get; set; }
    public long AmountCents { get; set; }
    public required string Reason { get; set; }
    /// <summary>jsonb, echoed in events.</summary>
    public List<RefundLineV1>? Lines { get; set; }
    public required string Status { get; set; }
    public string? StripeRefundId { get; set; }
    public string? FailureReason { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Stripe events, kept 90 days.</summary>
public class ProviderEvent
{
    public required string EventId { get; set; }
    public required string Type { get; set; }
    /// <summary>jsonb: the event body as received.</summary>
    public required string Payload { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public class DisputeRow
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public required string StripeDisputeId { get; set; }
    public required string Status { get; set; }
    public long AmountCents { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
