namespace Bloom.Order.Contracts;

/// <summary>Published by order when the order is paid and ready to pack (03-order §5.3); consumed by fulfillment.</summary>
public sealed record OrderReadyToFulfillV1(
    Guid OrderId, Guid AccountId, string Number, ShipToV1 ShipTo, string ShippingOption, long ShippingCents, long TotalCents,
    IReadOnlyList<ReadyLineV1> Lines);

/// <summary>The address snapshot of the order (03-order §4).</summary>
public sealed record ShipToV1(string Name, IReadOnlyList<string> Lines, string City, string State, string Zip, string Phone);

/// <param name="Kind"><c>product</c> or <c>warranty_plan</c>.</param>
/// <param name="ParentLineNo">The device line a plan covers; null on product lines.</param>
/// <param name="CoverageMonths">Null on product lines.</param>
public sealed record ReadyLineV1(
    int LineNo, string Kind, Guid SkuId, Guid ProductId, bool Serialized, int Quantity, int? ParentLineNo, int? CoverageMonths,
    long PaidCents, long TaxCents);
