namespace Bloom.Fulfillment.Contracts;

/// <summary>Message types fulfillment publishes (05-fulfillment §5) and the commands it sends inventory (02-inventory §5.3).</summary>
public static class FulfillmentMessages
{
    public const string ShipmentShipped = "fulfillment.shipment-shipped.v1";
    public const string ReceiveCustomerReturn = "inventory.receive-customer-return.v1";
    public const string ReceiveReturnedToSender = "inventory.receive-returned-to-sender.v1";
    public const string MarkLostInTransit = "inventory.mark-lost-in-transit.v1";
}
