using System.Text.RegularExpressions;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bloom.Inventory.Data;

public sealed partial class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockAllocation> StockAllocations => Set<StockAllocation>();
    public DbSet<SerialUnit> SerialUnits => Set<SerialUnit>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<LedgerEntry> StockLedger => Set<LedgerEntry>();

    private static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.AddBloomReliabilityTables();

        mb.Entity<StockItem>(e =>
        {
            e.ToTable("stock_items", t =>
            {
                t.HasCheckConstraint("ck_stock_items_reserved_le_on_hand", "reserved <= on_hand");
                foreach (var column in new[] { "on_hand", "reserved", "committed", "returned", "damaged", "lost" })
                    t.HasCheckConstraint($"ck_stock_items_{column}_non_negative", $"{column} >= 0");
            });
            e.HasKey(x => x.SkuId);
        });

        mb.Entity<StockAllocation>(e =>
        {
            e.ToTable("stock_allocations", t =>
            {
                t.HasCheckConstraint("ck_stock_allocations_kind", $"kind IN ({In(AllocationKinds.All)})");
                t.HasCheckConstraint("ck_stock_allocations_status", $"status IN ({In(AllocationStatuses.All)})");
                t.HasCheckConstraint("ck_stock_allocations_quantity", "quantity > 0");
            });
            e.HasKey(x => new { x.RefId, x.SkuId });
            e.HasIndex(x => new { x.SkuId, x.PaidAt, x.OrderId }).HasFilter("status = 'backordered'").HasDatabaseName("ix_stock_allocations_fifo");
            e.HasIndex(x => x.CreatedAt).HasFilter("status = 'reserved'").HasDatabaseName("ix_stock_allocations_stuck");
            e.HasIndex(x => x.OrderId).HasDatabaseName("ix_stock_allocations_order");
        });

        mb.Entity<SerialUnit>(e =>
        {
            e.ToTable("serial_units", t => t.HasCheckConstraint("ck_serial_units_status", $"status IN ({In(SerialStatuses.All)})"));
            e.HasKey(x => x.Serial);
            e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Imei).IsUnique().HasFilter("imei IS NOT NULL").HasDatabaseName("ux_serial_units_imei");
            e.HasIndex(x => new { x.SkuId, x.Status }).HasDatabaseName("ix_serial_units_sku_status");
            e.HasIndex(x => x.RefId).HasFilter("ref_id IS NOT NULL").HasDatabaseName("ix_serial_units_ref");
        });

        mb.Entity<GoodsReceipt>().ToTable("goods_receipts");

        mb.Entity<GoodsReceiptLine>(e =>
        {
            e.ToTable("goods_receipt_lines", t => t.HasCheckConstraint("ck_goods_receipt_lines_quantity", "quantity > 0"));
            e.HasKey(x => new { x.ReceiptId, x.SkuId });
            e.HasOne<GoodsReceipt>().WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<StockAdjustment>().ToTable("stock_adjustments", t =>
        {
            t.HasCheckConstraint("ck_stock_adjustments_delta", "delta <> 0");
            t.HasCheckConstraint("ck_stock_adjustments_reason_code", $"reason_code IN ({In(ReasonCodes.All)})");
        });

        mb.Entity<LedgerEntry>(e =>
        {
            e.ToTable("stock_ledger", t =>
            {
                t.HasCheckConstraint("ck_stock_ledger_entry_type", $"entry_type IN ({In(EntryTypes.All)})");
                t.HasCheckConstraint("ck_stock_ledger_ref_type", $"ref_type IN ({In(RefTypes.All)})");
            });
            e.HasIndex(x => new { x.SkuId, x.CreatedAt, x.Id }).HasDatabaseName("ix_stock_ledger_sku");
        });

        // Ids come from the app (UUIDv7); columns are snake_case. The reliability tables name their own columns.
        foreach (var entity in mb.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(StockItem).Namespace))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase().Replace(property.Name, "_").ToLowerInvariant());
            if (entity.FindProperty("Id") is { } id) id.ValueGenerated = ValueGenerated.Never;
        }

        // "DOnHand" would snake-case to "don_hand".
        mb.Entity<LedgerEntry>(e =>
        {
            e.Property(x => x.DOnHand).HasColumnName("d_on_hand");
            e.Property(x => x.DReserved).HasColumnName("d_reserved");
            e.Property(x => x.DCommitted).HasColumnName("d_committed");
            e.Property(x => x.DReturned).HasColumnName("d_returned");
            e.Property(x => x.DDamaged).HasColumnName("d_damaged");
            e.Property(x => x.DLost).HasColumnName("d_lost");
        });
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex SnakeCase();
}
