namespace Bloom.Fulfillment.Contracts;

/// <summary>Published by fulfillment when the shipment is handed over (05-fulfillment §5); consumed by inventory.</summary>
public sealed record ShipmentShippedV1(Guid OrderId, Guid RefId, IReadOnlyList<ShippedLineV1> Lines, string TrackingNumber);

public sealed record ShippedLineV1(Guid SkuId, int Quantity, IReadOnlyList<string> Serials);

/// <summary>Command from fulfillment: accepted units of a customer return (02-inventory §5.3). <c>Serial</c> for serialized SKUs.</summary>
public sealed record ReceiveCustomerReturnV1(Guid OrderId, IReadOnlyList<ReturnedUnitV1> Units);

public sealed record ReturnedUnitV1(Guid SkuId, string? Serial, int Quantity);

/// <summary>Command from fulfillment: units back from a failed delivery (02-inventory §5.3).</summary>
public sealed record ReceiveReturnedToSenderV1(Guid RefId, IReadOnlyList<ReturnedToSenderUnitV1> Units);

/// <summary><c>Outcome</c> is <c>sealed</c> or <c>failed</c>.</summary>
public sealed record ReturnedToSenderUnitV1(Guid SkuId, string? Serial, int Quantity, string Outcome);

/// <summary>Command from fulfillment: the carrier lost the shipment (02-inventory §5.3).</summary>
public sealed record MarkLostInTransitV1(Guid RefId);
