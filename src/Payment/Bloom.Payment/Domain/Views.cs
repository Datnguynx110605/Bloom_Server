using Bloom.Payment.Data;
using Google.Protobuf.WellKnownTypes;

namespace Bloom.Payment.Domain;

/// <summary>Table rows as the messages of <c>GetPayment</c> (04-payment §5.1): null text is <c>""</c>, a null time is unset.</summary>
public static class Views
{
    public static PaymentDetails Of(PaymentRow p) => new()
    {
        Id = Id(p.Id), OrderId = Id(p.OrderId), OrderNumber = p.OrderNumber, AccountId = Id(p.AccountId), AmountCents = p.AmountCents, Currency = p.Currency, Status = p.Status,
        StripeSessionId = p.StripeSessionId ?? "", StripePaymentIntentId = p.StripePaymentIntentId ?? "", CheckoutUrl = p.CheckoutUrl ?? "", SessionExpiresAt = Time(p.SessionExpiresAt),
        CancelRequested = p.CancelRequested, RefundedCents = p.RefundedCents, SucceededAt = Time(p.SucceededAt), Version = p.Version, CreatedAt = Time(p.CreatedAt), UpdatedAt = Time(p.UpdatedAt),
    };

    public static RefundDetails Of(RefundRow r) => new()
    {
        Id = Id(r.Id), PaymentId = Id(r.PaymentId), OrderId = Id(r.OrderId), AmountCents = r.AmountCents, Reason = r.Reason,
        Lines = { (r.Lines ?? []).Select(l => new RefundLine { LineNo = l.LineNo, AmountCents = l.AmountCents }) },
        Status = r.Status, StripeRefundId = r.StripeRefundId ?? "", FailureReason = r.FailureReason ?? "", Attempts = r.Attempts,
        NextAttemptAt = Time(r.NextAttemptAt), CreatedAt = Time(r.CreatedAt), UpdatedAt = Time(r.UpdatedAt),
    };

    public static DisputeDetails Of(DisputeRow d) => new()
    {
        Id = Id(d.Id), PaymentId = Id(d.PaymentId), StripeDisputeId = d.StripeDisputeId, Status = d.Status, AmountCents = d.AmountCents, Reason = d.Reason,
        OpenedAt = Time(d.OpenedAt), UpdatedAt = Time(d.UpdatedAt),
    };

    private static string Id(Guid id) => id.ToString("D");

    private static Timestamp? Time(DateTimeOffset? t) => t is { } v ? Timestamp.FromDateTimeOffset(v) : null;
}
