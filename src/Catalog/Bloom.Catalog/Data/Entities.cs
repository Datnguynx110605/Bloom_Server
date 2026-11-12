using NpgsqlTypes;

namespace Bloom.Catalog.Data;

// Column types, constraints and indexes are in CatalogDbContext; LLD 08-catalog §4.1.

public static class ProductKinds
{
    public const string Physical = "physical";
    public const string WarrantyPlan = "warranty_plan";

    public static readonly string[] All = [Physical, WarrantyPlan];
}

public static class ProductStatuses
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Archived = "archived";

    public static readonly string[] All = [Draft, Active, Archived];
}

public static class LaunchStages
{
    public const string Draft = "draft";
    public const string Announced = "announced";
    public const string PreorderOpen = "preorder_open";
    public const string Released = "released";

    public static readonly string[] All = [Draft, Announced, PreorderOpen, Released];

    /// <summary>Position in the launch sequence: draft 0 … released 3 (plan R23).</summary>
    public static int Rank(string stage) => Array.IndexOf(All, stage);
}

public static class ImageStatuses
{
    public const string Pending = "pending";
    public const string Ready = "ready";

    public static readonly string[] All = [Pending, Ready];
}

public static class ReviewStatuses
{
    public const string Visible = "visible";
    public const string Hidden = "hidden";

    public static readonly string[] All = [Visible, Hidden];
}

public class Category
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public int SortOrder { get; set; }
    public long Version { get; set; } = 1;
}

public class Product
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string DescriptionMd { get; set; } = "";
    /// <summary>Rendered from <see cref="DescriptionMd"/> on write (plan Task 4).</summary>
    public string DescriptionHtml { get; set; } = "";
    /// <summary><c>jsonb</c>: <c>[{group, items: [{label, value}]}]</c>, display-only.</summary>
    public string Specs { get; set; } = "[]";
    public bool Serialized { get; set; }
    public bool SerializedLocked { get; set; }
    public int CoverageMonths { get; set; }
    public string Status { get; set; } = ProductStatuses.Draft;
    public Guid? LaunchId { get; set; }
    public string? LaunchStage { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public long? MinPriceCents { get; set; }
    public int RatingSum { get; set; }
    public int RatingCount { get; set; }
    /// <summary>Maintained by the app (plan R12), not a generated column.</summary>
    public NpgsqlTsVector? SearchVector { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

public class OptionType
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
}

public class OptionValue
{
    public Guid Id { get; set; }
    public Guid OptionTypeId { get; set; }
    public required string Value { get; set; }
    public int Position { get; set; }
}

public class Sku
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public required string Code { get; set; }
    public string OptionKey { get; set; } = "";
    public long PriceCents { get; set; }
    public bool Active { get; set; } = true;
    public Guid? LaunchId { get; set; }
    public long Version { get; set; } = 1;
}

public class SkuOptionValue
{
    public Guid SkuId { get; set; }
    public Guid OptionValueId { get; set; }
}

public class PlanEligibility
{
    public Guid PlanProductId { get; set; }
    public Guid EligibleProductId { get; set; }
}

public class ProductImage
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid? SkuId { get; set; }
    public required string StorageKey { get; set; }
    public string? AltText { get; set; }
    public int Position { get; set; }
    public string Status { get; set; } = ImageStatuses.Pending;
    public DateTimeOffset CreatedAt { get; set; }
}

public class Review
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid? AccountId { get; set; }
    public required string AuthorDisplay { get; set; }
    public short Rating { get; set; }
    public string? Text { get; set; }
    public string Status { get; set; } = ReviewStatuses.Visible;
    public Guid? HiddenBy { get; set; }
    public string? HiddenReason { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ReviewEligibility
{
    public Guid AccountId { get; set; }
    public Guid ProductId { get; set; }
    public DateTimeOffset FirstDeliveredAt { get; set; }
}

public class ReviewerName
{
    public Guid AccountId { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
