namespace Bloom.Launch.Data;

// Column types, constraints and indexes are in LaunchDbContext; LLD 01-launch §4.1.

public static class LaunchStages
{
    public const string Draft = "draft";
    public const string Announced = "announced";
    public const string PreorderOpen = "preorder_open";
    public const string Released = "released";

    public static readonly string[] All = [Draft, Announced, PreorderOpen, Released];
}

public static class HoldStatuses
{
    public const string Held = "held";
    public const string Sold = "sold";
    public const string Released = "released";

    public static readonly string[] All = [Held, Sold, Released];
}

public class LaunchRow
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Stage { get; set; }
    public DateTimeOffset LaunchTime { get; set; }
    public DateTimeOffset ReleaseDate { get; set; }
    /// <summary>1 … the configured ceiling; the ceiling is not a column constraint.</summary>
    public int AdmissionRate { get; set; }
    /// <summary>Set when the waiting room is closed for good.</summary>
    public DateTimeOffset? WaitingRoomClosedAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class LaunchProductRow
{
    public Guid LaunchId { get; set; }
    /// <summary>Catalog product id; in one launch ever.</summary>
    public Guid ProductId { get; set; }
    public int PurchaseLimit { get; set; }
}

public class LaunchSkuRow
{
    public Guid LaunchId { get; set; }
    /// <summary>Catalog SKU id; in one launch ever.</summary>
    public Guid SkuId { get; set; }
    public Guid ProductId { get; set; }
    public int Allocation { get; set; }
    public int Held { get; set; }
    public int Sold { get; set; }
}

public class LimitUsageRow
{
    public Guid LaunchId { get; set; }
    public Guid AccountId { get; set; }
    public Guid ProductId { get; set; }
    public int Held { get; set; }
    public int Sold { get; set; }
}

public class HoldRow
{
    public Guid Id { get; set; }
    /// <summary>Unique: makes <c>HoldAllocation</c> idempotent.</summary>
    public Guid OrderId { get; set; }
    public Guid LaunchId { get; set; }
    public Guid AccountId { get; set; }
    public Guid PassId { get; set; }
    public required string Status { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class HoldLineRow
{
    public Guid HoldId { get; set; }
    public Guid SkuId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
}

public class ConsumedPassRow
{
    public Guid PassId { get; set; }
    public Guid LaunchId { get; set; }
    public Guid AccountId { get; set; }
    public Guid OrderId { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
}
