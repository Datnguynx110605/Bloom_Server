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

/// <summary>Published when the pack task of a <c>kind = order</c> task starts (05-fulfillment §5.3).</summary>
public sealed record PackingStartedV1(Guid OrderId);

/// <summary>Published when the carrier delivers; <c>RefId</c> is the shipment id (task) or the reship id.</summary>
public sealed record ShipmentDeliveredV1(Guid OrderId, Guid RefId, DateTimeOffset DeliveredAt);

/// <summary><c>Kind</c> is the failure kind: <c>lost</c> or <c>returned_to_sender</c>.</summary>
public sealed record DeliveryFailedV1(Guid OrderId, string Kind);

public sealed record ReturnRequestedV1(Guid OrderId, Guid ReturnId);

/// <summary><c>Reason</c> is <c>decided</c> or <c>expired</c>.</summary>
public sealed record ReturnClosedV1(Guid OrderId, Guid ReturnId, string Reason);

public sealed record ReturnLabelIssuedV1(Guid OrderId, Guid ReturnId, string LabelUrl);

public sealed record ReturnReceivedV1(Guid OrderId, Guid ReturnId);

public sealed record ReturnDecidedV1(Guid OrderId, Guid ReturnId, IReadOnlyList<AcceptedLineV1> Accepted, long RefundCents);

public sealed record AcceptedLineV1(int LineNo, int Quantity);
