namespace Bloom.Order.Contracts;

/// <summary>Published by order when the shipment is delivered (03-order §5.3); consumed by catalog for review eligibility.</summary>
public sealed record OrderDeliveredV1(Guid OrderId, Guid AccountId, IReadOnlyList<Guid> ProductIds, DateTimeOffset DeliveredAt);
