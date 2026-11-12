namespace Bloom.Fulfillment.Data;

// Column types, constraints and indexes are in FulfillmentDbContext; LLD 05-fulfillment §4 and spec §4/§7.

public static class TaskKinds
{
    public const string Order = "order";
    public const string Reship = "reship";

    public static readonly string[] All = [Order, Reship];
}

public static class TaskStatuses
{
    public const string Pending = "pending";
    public const string Packing = "packing";
    public const string Packed = "packed";
    public const string HandedOver = "handed_over";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Pending, Packing, Packed, HandedOver, Cancelled];
}

public static class LineKinds
{
    public const string Product = "product";
    public const string WarrantyPlan = "warranty_plan";

    public static readonly string[] All = [Product, WarrantyPlan];
}

/// <summary>Shipping service levels: <c>pack_tasks.shipping_option</c> and <c>shipments.service</c> (LLD §4.1).</summary>
public static class ShippingOptions
{
    public const string Standard = "standard";
    public const string Express = "express";

    public static readonly string[] All = [Standard, Express];
}

public static class Directions
{
    public const string Outbound = "outbound";
    public const string Return = "return";
    public const string ShipBack = "ship_back";

    public static readonly string[] All = [Outbound, Return, ShipBack];
}

public static class ShipmentStatuses
{
    public const string LabelPending = "label_pending";
    public const string LabelReady = "label_ready";
    public const string HandedOver = "handed_over";
    public const string InTransit = "in_transit";
    public const string Delivered = "delivered";
    public const string PossiblyLost = "possibly_lost";
    public const string Lost = "lost";
    public const string ReturnedToSender = "returned_to_sender";
    public const string VoidPending = "void_pending";
    public const string Voided = "voided";

    public static readonly string[] All =
        [LabelPending, LabelReady, HandedOver, InTransit, Delivered, PossiblyLost, Lost, ReturnedToSender, VoidPending, Voided];
}

public static class ReturnStatuses
{
    public const string Requested = "requested";
    public const string LabelIssued = "label_issued";
    public const string InTransit = "in_transit";
    public const string Received = "received";
    public const string Inspected = "inspected";
    public const string Decided = "decided";
    public const string Expired = "expired";

    public static readonly string[] All = [Requested, LabelIssued, InTransit, Received, Inspected, Decided, Expired];
}

public static class InspectionResults
{
    public const string Ok = "ok";
    public const string Damaged = "damaged";
    public const string MissingParts = "missing_parts";
    public const string WrongItem = "wrong_item";

    public static readonly string[] All = [Ok, Damaged, MissingParts, WrongItem];
}

public static class Decisions
{
    public const string Full = "full";
    public const string Partial = "partial";
    public const string Reject = "reject";

    public static readonly string[] All = [Full, Partial, Reject];
}

public static class FailureKinds
{
    public const string ReturnedToSender = "returned_to_sender";
    public const string Lost = "lost";

    public static readonly string[] All = [ReturnedToSender, Lost];
}

public static class FailureStatuses
{
    public const string PossiblyLost = "possibly_lost";
    public const string Confirmed = "confirmed";
    public const string ResolvedRefund = "resolved_refund";
    public const string ResolvedReship = "resolved_reship";
    public const string Dismissed = "dismissed";

    public static readonly string[] All = [PossiblyLost, Confirmed, ResolvedRefund, ResolvedReship, Dismissed];
}

public static class SagaTypes
{
    public const string ReturnRefund = "return_refund";
    public const string DeliveryFailure = "delivery_failure";

    public static readonly string[] All = [ReturnRefund, DeliveryFailure];
}

public static class CoverageStatuses
{
    public const string PendingDelivery = "pending_delivery";
    public const string Active = "active";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired"; // D30: derived at read (GetCoverage), never stored, so not in All

    public static readonly string[] All = [PendingDelivery, Active, Cancelled];
}

public class PackTask
{
    public Guid Id { get; set; }
    /// <summary>The order id, or the reship id for reships (unique).</summary>
    public Guid RefId { get; set; }
    public Guid OrderId { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public bool CancelledBeforeArrival { get; set; }
    /// <summary>jsonb <c>ShipToV1</c>; null on cancellation markers.</summary>
    public string? ShipTo { get; set; }
    public string? ShippingOption { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PackTaskLine
{
    public Guid TaskId { get; set; }
    public int LineNo { get; set; }
    public required string Kind { get; set; }
    public Guid SkuId { get; set; }
    public Guid ProductId { get; set; }
    public bool Serialized { get; set; }
    public int Quantity { get; set; }
    public int? ParentLineNo { get; set; }
    public int? CoverageMonths { get; set; }
}

public class PackTaskSerial
{
    public Guid TaskId { get; set; }
    public required string Serial { get; set; }
    public int LineNo { get; set; }
    /// <summary>Scan order within the task.</summary>
    public int Seq { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
}

public class Shipment
{
    public Guid Id { get; set; }
    /// <summary>Null for return labels.</summary>
    public Guid? PackTaskId { get; set; }
    /// <summary>Null unless direction is <c>return</c> or <c>ship_back</c>.</summary>
    public Guid? ReturnRequestId { get; set; }
    public Guid OrderId { get; set; }
    public required string Direction { get; set; }
    public required string Service { get; set; }
    public required string Status { get; set; }
    public string? ShippoTransactionId { get; set; }
    public string? TrackingNumber { get; set; }
    public string? LabelUrl { get; set; }
    public DateTimeOffset? LastTrackingAt { get; set; }
    public DateTimeOffset? HandedOverAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int LabelAttempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class OrderAccount
{
    public Guid OrderId { get; set; }
    public Guid AccountId { get; set; }
    public long ShippingCents { get; set; }
    public long TotalCents { get; set; }
    public bool ShippingRefunded { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public class OrderLineAccount
{
    public Guid OrderId { get; set; }
    public int LineNo { get; set; }
    public required string Kind { get; set; }
    public int? ParentLineNo { get; set; }
    public int Quantity { get; set; }
    public long PaidCents { get; set; }
    public long TaxCents { get; set; }
    public int RefundedUnits { get; set; }
    public long RefundedCents { get; set; }
}

public class ReturnRequest
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid AccountId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? ReturnShipmentId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ReturnUnit
{
    public Guid ReturnId { get; set; }
    public int UnitNo { get; set; }
    public int LineNo { get; set; }
    public string? ReceivedSerial { get; set; }
    public string? InspectionResult { get; set; }
    public string? InspectionNotes { get; set; }
    public string? Decision { get; set; }
    public long RefundCents { get; set; }
    public long RefundTaxCents { get; set; }
}

public class DeliveryFailure
{
    public Guid ShipmentId { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    /// <summary>The shipment's status before a <c>possibly_lost</c> flag; restored on dismissal (spec §4).</summary>
    public string? PreviousStatus { get; set; }
    /// <summary>jsonb: per unit <c>sealed</c> | <c>failed</c>.</summary>
    public string? RtsOutcomes { get; set; }
    public Guid? ReshipId { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}

public class FulfillmentSaga
{
    /// <summary><c>return_id</c> for <c>return_refund</c>, <c>shipment_id</c> for <c>delivery_failure</c> (plan R19).</summary>
    public Guid RefId { get; set; }
    public required string Type { get; set; }
    public string[] PendingSteps { get; set; } = [];
    public Guid RefundId { get; set; }
    /// <summary>That refund's per-line amounts (<c>[{LineNo, AmountCents}]</c>), given back when the refund fails (D35). Null for a saga created before D35; the failure consumer empties it once applied.</summary>
    public string? RefundLines { get; set; }
    /// <summary>True when that refund set <c>order_accounts.shipping_refunded</c> (D35). Null for a saga created before D35.</summary>
    public bool? RefundedShipping { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public class WarrantyCoverage
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int PlanLineNo { get; set; }
    public int DeviceLineNo { get; set; }
    public required string Serial { get; set; }
    public int CoverageMonths { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
}
