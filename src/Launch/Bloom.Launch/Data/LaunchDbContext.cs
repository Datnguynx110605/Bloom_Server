using System.Text.RegularExpressions;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bloom.Launch.Data;

public sealed partial class LaunchDbContext(DbContextOptions<LaunchDbContext> options) : DbContext(options)
{
    public DbSet<LaunchRow> Launches => Set<LaunchRow>();
    public DbSet<LaunchProductRow> LaunchProducts => Set<LaunchProductRow>();
    public DbSet<LaunchSkuRow> LaunchSkus => Set<LaunchSkuRow>();
    public DbSet<LimitUsageRow> LimitUsage => Set<LimitUsageRow>();
    public DbSet<HoldRow> Holds => Set<HoldRow>();
    public DbSet<HoldLineRow> HoldLines => Set<HoldLineRow>();
    public DbSet<ConsumedPassRow> ConsumedPasses => Set<ConsumedPassRow>();

    private static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.AddBloomReliabilityTables();

        // No timestamp defaults: every stored timestamp is the database's now(), written by the statement (plan R6).
        mb.Entity<LaunchRow>(e =>
        {
            e.ToTable("launches", t =>
            {
                t.HasCheckConstraint("ck_launches_stage", $"stage IN ({In(LaunchStages.All)})");
                t.HasCheckConstraint("ck_launches_release_after_launch", "release_date > launch_time");
                t.HasCheckConstraint("ck_launches_admission_rate", "admission_rate >= 1");
            });
            e.Property(x => x.Version).HasDefaultValue(0L);
            e.HasIndex(x => x.Stage).HasDatabaseName("ix_launches_stage");
        });

        mb.Entity<LaunchProductRow>(e =>
        {
            e.ToTable("launch_products", t => t.HasCheckConstraint("ck_launch_products_limit_positive", "purchase_limit > 0"));
            e.HasKey(x => new { x.LaunchId, x.ProductId });
            e.HasOne<LaunchRow>().WithMany().HasForeignKey(x => x.LaunchId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ProductId).IsUnique().HasDatabaseName("ux_launch_products_product_id");
        });

        mb.Entity<LaunchSkuRow>(e =>
        {
            e.ToTable("launch_skus", t =>
            {
                t.HasCheckConstraint("ck_launch_skus_non_negative", "allocation >= 0 AND held >= 0 AND sold >= 0");
                t.HasCheckConstraint("ck_launch_skus_within_allocation", "held + sold <= allocation");
            });
            e.HasKey(x => new { x.LaunchId, x.SkuId });
            e.Property(x => x.Held).HasDefaultValue(0);
            e.Property(x => x.Sold).HasDefaultValue(0);
            e.HasOne<LaunchProductRow>().WithMany().HasForeignKey(x => new { x.LaunchId, x.ProductId }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SkuId).IsUnique().HasDatabaseName("ux_launch_skus_sku_id");
            // The composite FK's columns are not a prefix of the PK, so EF indexes them; only the name is ours.
            e.HasIndex(x => new { x.LaunchId, x.ProductId }).HasDatabaseName("ix_launch_skus_launch_id_product_id");
        });

        mb.Entity<LimitUsageRow>(e =>
        {
            e.ToTable("limit_usage", t => t.HasCheckConstraint("ck_limit_usage_non_negative", "held >= 0 AND sold >= 0"));
            e.HasKey(x => new { x.LaunchId, x.AccountId, x.ProductId });
            e.Property(x => x.Held).HasDefaultValue(0);
            e.Property(x => x.Sold).HasDefaultValue(0);
        });

        mb.Entity<HoldRow>(e =>
        {
            e.ToTable("holds", t => t.HasCheckConstraint("ck_holds_status", $"status IN ({In(HoldStatuses.All)})"));
            e.Property(x => x.Version).HasDefaultValue(0L);
            e.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("ux_holds_order_id");
            e.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_holds_held_created_at")
                .HasFilter($"status = '{HoldStatuses.Held}'");
        });

        mb.Entity<HoldLineRow>(e =>
        {
            e.ToTable("hold_lines", t => t.HasCheckConstraint("ck_hold_lines_quantity_positive", "quantity > 0"));
            e.HasKey(x => new { x.HoldId, x.SkuId });
            e.HasOne<HoldRow>().WithMany().HasForeignKey(x => x.HoldId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<ConsumedPassRow>(e =>
        {
            e.ToTable("consumed_passes");
            e.HasKey(x => x.PassId);
        });

        // Ids come from the app (UUIDv7); columns are snake_case. The reliability tables name their own columns.
        foreach (var entity in mb.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(LaunchRow).Namespace))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase().Replace(property.Name, "_").ToLowerInvariant());
            if (entity.FindProperty("Id") is { } id && id.ClrType == typeof(Guid)) id.ValueGenerated = ValueGenerated.Never;
        }
    }

    [GeneratedRegex("(?<!^)(?=[A-Z])")]
    private static partial Regex SnakeCase();

    /// <summary>The database's <c>now()</c>, the clock of every check and stored timestamp inside a transaction (plan R6).</summary>
    public Task<DateTimeOffset> NowAsync(CancellationToken ct) =>
        Database.SqlQuery<DateTimeOffset>($"SELECT now() AS \"Value\"").SingleAsync(ct);
}
