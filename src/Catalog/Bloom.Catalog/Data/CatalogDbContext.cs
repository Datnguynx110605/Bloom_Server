using System.Text.RegularExpressions;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bloom.Catalog.Data;

public sealed partial class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<OptionType> OptionTypes => Set<OptionType>();
    public DbSet<OptionValue> OptionValues => Set<OptionValue>();
    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<SkuOptionValue> SkuOptionValues => Set<SkuOptionValue>();
    public DbSet<PlanEligibility> PlanEligibilities => Set<PlanEligibility>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<ReviewEligibility> ReviewEligibilities => Set<ReviewEligibility>();
    public DbSet<ReviewerName> ReviewerNames => Set<ReviewerName>();

    private static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.AddBloomReliabilityTables();

        mb.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_categories_slug");
        });

        mb.Entity<Product>(e =>
        {
            e.ToTable("products", t =>
            {
                t.HasCheckConstraint("ck_products_kind", $"kind IN ({In(ProductKinds.All)})");
                t.HasCheckConstraint("ck_products_status", $"status IN ({In(ProductStatuses.All)})");
                t.HasCheckConstraint("ck_products_launch_stage", $"launch_stage IS NULL OR launch_stage IN ({In(LaunchStages.All)})");
            });
            e.Property(x => x.Specs).HasColumnType("jsonb");
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_products_slug");
            e.HasIndex(x => x.SearchVector).HasMethod("gin").HasDatabaseName("ix_products_search_vector");
            e.HasIndex(x => new { x.CategoryId, x.Status }).HasDatabaseName("ix_products_category_status");
            e.HasIndex(x => x.MinPriceCents).HasDatabaseName("ix_products_min_price_cents");
            e.HasIndex(x => x.PublishedAt).HasDatabaseName("ix_products_published_at");
        });

        mb.Entity<OptionType>(e =>
        {
            e.ToTable("option_types");
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProductId).HasDatabaseName("ix_option_types_product_id");
        });

        mb.Entity<OptionValue>(e =>
        {
            e.ToTable("option_values");
            e.HasOne<OptionType>().WithMany().HasForeignKey(x => x.OptionTypeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.OptionTypeId).HasDatabaseName("ix_option_values_option_type_id");
        });

        mb.Entity<Sku>(e =>
        {
            e.ToTable("skus", t => t.HasCheckConstraint("ck_skus_price_cents", "price_cents > 0"));
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_skus_code");
            e.HasIndex(x => new { x.ProductId, x.OptionKey }).IsUnique().HasDatabaseName("ux_skus_product_option_key");
        });

        mb.Entity<SkuOptionValue>(e =>
        {
            e.ToTable("sku_option_values");
            e.HasKey(x => new { x.SkuId, x.OptionValueId });
            e.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<OptionValue>().WithMany().HasForeignKey(x => x.OptionValueId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<PlanEligibility>(e =>
        {
            e.ToTable("plan_eligibility");
            e.HasKey(x => new { x.PlanProductId, x.EligibleProductId });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.PlanProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.EligibleProductId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<ProductImage>(e =>
        {
            e.ToTable("product_images", t => t.HasCheckConstraint("ck_product_images_status", $"status IN ({In(ImageStatuses.All)})"));
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<Review>(e =>
        {
            e.ToTable("reviews", t =>
            {
                t.HasCheckConstraint("ck_reviews_status", $"status IN ({In(ReviewStatuses.All)})");
                t.HasCheckConstraint("ck_reviews_rating", "rating BETWEEN 1 AND 5");
            });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ProductId, x.AccountId }).IsUnique().HasFilter("account_id IS NOT NULL").HasDatabaseName("ux_reviews_product_account");
            e.HasIndex(x => new { x.ProductId, x.CreatedAt }).HasDatabaseName("ix_reviews_product_created_at");
        });

        mb.Entity<ReviewEligibility>(e =>
        {
            e.ToTable("review_eligibility");
            e.HasKey(x => new { x.AccountId, x.ProductId });
        });

        mb.Entity<ReviewerName>(e =>
        {
            e.ToTable("reviewer_names");
            e.HasKey(x => x.AccountId);
        });

        // Ids come from the app (UUIDv7); columns are snake_case. The reliability tables name their own columns.
        foreach (var entity in mb.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(Category).Namespace))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase().Replace(property.Name, "_").ToLowerInvariant());
            if (entity.FindProperty("Id") is { } id) id.ValueGenerated = ValueGenerated.Never;
        }
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex SnakeCase();
}
