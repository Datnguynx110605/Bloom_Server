namespace Bloom.Fulfillment.Contracts;

/// <summary>Message types fulfillment publishes (05-fulfillment §5) and the commands it sends inventory (02-inventory §5.3).</summary>
public static class FulfillmentMessages
{
    public const string PackingStarted = "fulfillment.packing-started.v1";
    public const string ShipmentShipped = "fulfillment.shipment-shipped.v1";
    public const string ShipmentDelivered = "fulfillment.shipment-delivered.v1";
    public const string DeliveryFailed = "fulfillment.delivery-failed.v1";
    public const string ReturnRequested = "fulfillment.return-requested.v1";
    public const string ReturnClosed = "fulfillment.return-closed.v1";
    public const string ReturnLabelIssued = "fulfillment.return-label-issued.v1";
    public const string ReturnReceived = "fulfillment.return-received.v1";
    public const string ReturnDecided = "fulfillment.return-decided.v1";
    public const string ReceiveCustomerReturn = "inventory.receive-customer-return.v1";
    public const string ReceiveReturnedToSender = "inventory.receive-returned-to-sender.v1";
    public const string MarkLostInTransit = "inventory.mark-lost-in-transit.v1";
}
