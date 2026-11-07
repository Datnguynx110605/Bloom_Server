namespace Bloom.Order.Contracts;

/// <summary>Published by order when an order is cancelled (03-order §5.3).</summary>
public sealed record OrderCancelledV1(Guid OrderId, string OrderNumber, Guid AccountId, string Reason);
