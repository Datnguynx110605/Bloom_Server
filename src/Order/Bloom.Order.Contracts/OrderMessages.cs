namespace Bloom.Order.Contracts;

/// <summary>Message types of 03-order §5.3.</summary>
public static class OrderMessages
{
    public const string Paid = "order.paid.v1";
    public const string Delivered = "order.delivered.v1";
}
