namespace Bloom.Payment.Contracts;

/// <summary>Command to payment: refund part of an order's payment. <c>Reason</c> is <c>return</c> or <c>delivery_failure</c> when fulfillment sends it. <c>Lines</c> is absent on order's own refunds.</summary>
public sealed record RefundPaymentV1(Guid OrderId, Guid RefundId, long AmountCents, string Reason, IReadOnlyList<RefundLineV1>? Lines);

public sealed record RefundLineV1(int LineNo, long AmountCents);

/// <summary>Published by payment when the refund is settled.</summary>
public sealed record RefundCompletedV1(Guid OrderId, Guid RefundId, long AmountCents, IReadOnlyList<RefundLineV1>? Lines);

/// <summary>Published by payment when the refund could not be made.</summary>
public sealed record RefundFailedV1(Guid OrderId, Guid RefundId, string FailureReason);
