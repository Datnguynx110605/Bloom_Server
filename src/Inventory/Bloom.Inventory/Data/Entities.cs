namespace Bloom.Inventory.Data;

// Column types, constraints and indexes are in InventoryDbContext; LLD 02-inventory §4.

public static class AllocationKinds
{
    public const string Regular = "regular";
    public const string Preorder = "preorder";
    public const string Reship = "reship";

    public static readonly string[] All = [Regular, Preorder, Reship];
}

public static class AllocationStatuses
{
    public const string Reserved = "reserved";
    public const string Backordered = "backordered";
    public const string Committed = "committed";
    public const string Shipped = "shipped";
    public const string Released = "released";

    public static readonly string[] All = [Reserved, Backordered, Committed, Shipped, Released];
}

public static class SerialStatuses
{
    public const string InStock = "in_stock";
    public const string Assigned = "assigned";
    public const string Shipped = "shipped";
    public const string Returned = "returned";
    public const string Damaged = "damaged";
    public const string Lost = "lost";

    public static readonly string[] All = [InStock, Assigned, Shipped, Returned, Damaged, Lost];
}

public static class EntryTypes
{
    public const string Received = "received";
    public const string Reserved = "reserved";
    public const string ReservationReleased = "reservation_released";
    public const string Committed = "committed";
    public const string BackorderAllocated = "backorder_allocated";
    public const string CommitmentReleased = "commitment_released";
    public const string Shipped = "shipped";
    public const string CustomerReturned = "customer_returned";
    public const string RtsRestocked = "rts_restocked";
    public const string RtsDamaged = "rts_damaged";
    public const string LostInTransit = "lost_in_transit";
    public const string Adjusted = "adjusted";

    public static readonly string[] All =
    [
        Received, Reserved, ReservationReleased, Committed, BackorderAllocated, CommitmentReleased,
        Shipped, CustomerReturned, RtsRestocked, RtsDamaged, LostInTransit, Adjusted,
    ];
}

public static class RefTypes
{
    public const string Order = "order";
    public const string Reship = "reship";
    public const string Receipt = "receipt";
    public const string Adjustment = "adjustment";

    public static readonly string[] All = [Order, Reship, Receipt, Adjustment];

    /// <summary>Plan R11: the ledger <c>ref_type</c> of an allocation change follows the allocation's kind.</summary>
    public static string Of(string? allocationKind) => allocationKind == AllocationKinds.Reship ? Reship : Order;
}

public static class ReasonCodes
{
    public const string DamagedInWarehouse = "damaged_in_warehouse";
    public const string LostInWarehouse = "lost_in_warehouse";
    public const string Found = "found";
    public const string CountCorrection = "count_correction";
    public const string Other = "other";

    public static readonly string[] All = [DamagedInWarehouse, LostInWarehouse, Found, CountCorrection, Other];
}

public class StockItem
{
    public Guid SkuId { get; set; }
    public Guid ProductId { get; set; }
    public bool Serialized { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public int Committed { get; set; }
    public int Returned { get; set; }
    public int Damaged { get; set; }
    public int Lost { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>No foreign key to <see cref="StockItem"/>: a pre-order exists before the SKU's first receipt.</summary>
public class StockAllocation
{
    /// <summary>The order id, or the reship id for reships.</summary>
    public Guid RefId { get; set; }
    public Guid SkuId { get; set; }
    public Guid OrderId { get; set; }
    public required string Kind { get; set; }
    public int Quantity { get; set; }
    public required string Status { get; set; }
    /// <summary>Set for pre-orders: the FIFO key.</summary>
    public DateTimeOffset? PaidAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class SerialUnit
{
    public required string Serial { get; set; }
    public string? Imei { get; set; }
    public Guid SkuId { get; set; }
    public required string Status { get; set; }
    public Guid? RefId { get; set; }
    public Guid ReceiptId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class GoodsReceipt
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public string Note { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}

public class GoodsReceiptLine
{
    public Guid ReceiptId { get; set; }
    public Guid SkuId { get; set; }
    public int Quantity { get; set; }
}

public class StockAdjustment
{
    public Guid Id { get; set; }
    public Guid SkuId { get; set; }
    public string? Serial { get; set; }
    public int Delta { get; set; }
    public required string ReasonCode { get; set; }
    public required string Note { get; set; }
    public Guid StaffId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Append-only: the app role has INSERT and SELECT only (IR-03, plan R8).</summary>
public class LedgerEntry
{
    public Guid Id { get; set; }
    public Guid SkuId { get; set; }
    public required string EntryType { get; set; }
    public int DOnHand { get; set; }
    public int DReserved { get; set; }
    public int DCommitted { get; set; }
    public int DReturned { get; set; }
    public int DDamaged { get; set; }
    public int DLost { get; set; }
    public required string RefType { get; set; }
    public Guid RefId { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
