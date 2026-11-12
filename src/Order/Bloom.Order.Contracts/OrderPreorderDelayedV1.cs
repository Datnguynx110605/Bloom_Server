namespace Bloom.Order.Contracts;

/// <summary>Published by order when a paid pre-order is not ready at release (03-order §5.3).</summary>
public sealed record OrderPreorderDelayedV1(Guid OrderId, string OrderNumber, Guid AccountId);
