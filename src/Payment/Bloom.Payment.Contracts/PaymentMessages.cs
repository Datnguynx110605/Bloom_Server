namespace Bloom.Payment.Contracts;

/// <summary>Message types of 04-payment §5: the refund command and its two outcomes.</summary>
public static class PaymentMessages
{
    public const string RefundPayment = "payment.refund-payment.v1";
    public const string RefundCompleted = "payment.refund-completed.v1";
    public const string RefundFailed = "payment.refund-failed.v1";
}
