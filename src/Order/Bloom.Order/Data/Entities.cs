using Bloom.Order.Contracts;

namespace Bloom.Order.Data;

// Column types, constraints and indexes are in OrderDbContext; LLD 03-order §4 and spec §4.

public static class OrderKinds
{
    public const string Regular = "regular";
    public const string Launch = "launch";

    public static readonly string[] All = [Regular, Launch];
}

public static class OrderStatuses
{
    public const string Creating = "creating";
    public const string PendingPayment = "pending_payment";
    public const string Paid = "paid";
    public const string AwaitingStock = "awaiting_stock";
    public const string Packing = "packing";
    public const string Shipped = "shipped";
    public const string Delivered = "delivered";
    public const string Cancelled = "cancelled";
    public const string DeliveryFailed = "delivery_failed";
    public const string PartiallyReturned = "partially_returned";
    public const string Returned = "returned";

    public static readonly string[] All =
        [Creating, PendingPayment, Paid, AwaitingStock, Packing, Shipped, Delivered, Cancelled, DeliveryFailed, PartiallyReturned, Returned];
}

public static class CancelReasons
{
    public const string CheckoutFailed = "checkout_failed";
    public const string CheckoutExpired = "checkout_expired";
    public const string Customer = "customer";
    public const string Support = "support";

    public static readonly string[] All = [CheckoutFailed, CheckoutExpired, Customer, Support];
}

public static class LineKinds
{
    public const string Product = "product";
    public const string WarrantyPlan = "warranty_plan";

    public static readonly string[] All = [Product, WarrantyPlan];
}

public static class ShippingOptions
{
    public const string Standard = "standard";
    public const string Express = "express";

    public static readonly string[] All = [Standard, Express];
}

public static class ActorTypes
{
    public const string Customer = "customer";
    public const string Staff = "staff";
    public const string System = "system";

    public static readonly string[] All = [Customer, Staff, System];
}

public static class SagaTypes
{
    public const string CheckoutCompensation = "checkout_compensation";
    public const string Expiry = "expiry";
    public const string Cancellation = "cancellation";
    public const string LatePayment = "late_payment";

    public static readonly string[] All = [CheckoutCompensation, Expiry, Cancellation, LatePayment];
}

public static class SagaSteps
{
    public const string InventoryRelease = "inventory_release";
    public const string LaunchRelease = "launch_release";
    public const string Refund = "refund";
    /// <summary>Replaces <c>refund</c> after <c>payment.refund-failed.v1</c>, so the saga stays open (spec §4).</summary>
    public const string RefundFailed = "refund_failed";

    public static readonly string[] All = [InventoryRelease, LaunchRelease, Refund, RefundFailed];
}

public static class CouponTypes
{
    public const string Percent = "percent";
    public const string Fixed = "fixed";

    public static readonly string[] All = [Percent, Fixed];
}

public static class CouponScopes
{
    public const string Order = "order";
    public const string Selected = "selected";

    public static readonly string[] All = [Order, Selected];
}

public static class TargetTypes
{
    public const string Product = "product";
    public const string Category = "category";

    public static readonly string[] All = [Product, Category];
}

public static class CouponUseStatuses
{
    public const string Held = "held";
    public const string Consumed = "consumed";
    public const string Released = "released";
    public const string Restored = "restored";

    public static readonly string[] All = [Held, Consumed, Released, Restored];
}

public class OrderRow
{
    public Guid Id { get; set; }
    public required string Number { get; set; }
    public Guid AccountId { get; set; }
    public required string Kind { get; set; }
    public Guid? LaunchId { get; set; }
    public required string Status { get; set; }
    public string? CancelReason { get; set; }
    public bool StockAllocated { get; set; }
    public bool LaunchReleased { get; set; }
    public bool HandedToFulfillment { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public required string ShippingOption { get; set; }
    /// <summary>jsonb address snapshot.</summary>
    public required ShipToV1 ShipTo { get; set; }
    public required string Currency { get; set; }
    public long SubtotalCents { get; set; }
    public long DiscountCents { get; set; }
    public long TaxCents { get; set; }
    public long ShippingCents { get; set; }
    public long TotalCents { get; set; }
    /// <summary>Basis points locked at checkout start.</summary>
    public int TaxRateBp { get; set; }
    public Guid? PaymentId { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class OrderLine
{
    public Guid OrderId { get; set; }
    public int LineNo { get; set; }
    public required string Kind { get; set; }
    public Guid SkuId { get; set; }
    public Guid ProductId { get; set; }
    public Guid CategoryId { get; set; }
    public required string Name { get; set; }
    public bool Serialized { get; set; }
    public int? ParentLineNo { get; set; }
    public int? CoverageMonths { get; set; }
    public int Quantity { get; set; }
    public long UnitPriceCents { get; set; }
    public long DiscountCents { get; set; }
    public long TaxCents { get; set; }
    public long PaidCents { get; set; }
    public int ReturnedQty { get; set; }
    public long RefundedCents { get; set; }
}

public class OrderStatusHistory
{
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    /// <summary>Null on the first row of an order.</summary>
    public string? FromStatus { get; set; }
    public required string ToStatus { get; set; }
    public required string ActorType { get; set; }
    public Guid? ActorId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; }
}

public class OrderSaga
{
    public Guid OrderId { get; set; }
    public required string Type { get; set; }
    public string[] PendingSteps { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Finds the order of a resumed StartCheckout (spec §4).</summary>
public class CheckoutKey
{
    public Guid AccountId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public Guid OrderId { get; set; }
}

/// <summary>One row per return (spec §4): open while <see cref="ClosedAt"/> is null.</summary>
public class OrderReturn
{
    public Guid OrderId { get; set; }
    public Guid ReturnId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

public class Cart
{
    public Guid AccountId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class CartLine
{
    public Guid AccountId { get; set; }
    public int LineNo { get; set; }
    public required string Kind { get; set; }
    public Guid SkuId { get; set; }
    public int? ParentLineNo { get; set; }
    public int Quantity { get; set; }
}

public class Coupon
{
    public Guid Id { get; set; }
    /// <summary>Upper-cased, trimmed (PRM-R12).</summary>
    public required string CodeNormalized { get; set; }
    public required string CodeDisplay { get; set; }
    public required string Type { get; set; }
    /// <summary>Percent: 1–100; fixed: cents &gt; 0.</summary>
    public int Value { get; set; }
    public required string Scope { get; set; }
    public long? MinSpendCents { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? TotalLimit { get; set; }
    public int? PerCustomerLimit { get; set; }
    /// <summary>Held + consumed.</summary>
    public int UsesCount { get; set; }
    public bool Active { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class CouponScopeItem
{
    public Guid CouponId { get; set; }
    public required string TargetType { get; set; }
    public Guid TargetId { get; set; }
}

public class CouponCustomerUsage
{
    public Guid CouponId { get; set; }
    public Guid AccountId { get; set; }
    public int UsesCount { get; set; }
}

public class CouponUse
{
    public Guid OrderId { get; set; }
    public Guid CouponId { get; set; }
    public Guid AccountId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The single settings row (<c>id = 1</c>, seeded by the migration).</summary>
public class CheckoutSettings
{
    public short Id { get; set; }
    public long ExpressFeeCents { get; set; }
    public long Version { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
