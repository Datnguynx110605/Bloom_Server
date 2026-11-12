namespace Bloom.Order.Contracts;

/// <summary>Message types of 03-order §5.3.</summary>
public static class OrderMessages
{
    public const string Paid = "order.paid.v1";
    public const string Delivered = "order.delivered.v1";
    public const string ReadyToFulfill = "order.ready-to-fulfill.v1";
    public const string Cancelled = "order.cancelled.v1";
    public const string PreorderDelayed = "order.preorder-delayed.v1";
}
