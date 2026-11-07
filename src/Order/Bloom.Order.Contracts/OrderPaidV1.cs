using System.Text.Json.Serialization;

namespace Bloom.Order.Contracts;

/// <summary>Published by order when payment is confirmed (03-order §5.3); consumed by inventory. <paramref name="LaunchId"/> is null for a regular order; it is written as an explicit <c>null</c>, because the envelope's options leave nulls out and the reader requires the property.</summary>
/// <param name="Lines">Product lines only, never warranty plans (LCH-R13): inventory backorders every line of a pre-order.</param>
public sealed record OrderPaidV1(Guid OrderId, string OrderNumber, Guid AccountId, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? LaunchId, DateTimeOffset PaidAt, long TotalCents, IReadOnlyList<OrderPaidLineV1> Lines);

public sealed record OrderPaidLineV1(Guid SkuId, int Quantity);
