using System.Text.RegularExpressions;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bloom.Fulfillment.Data;

public sealed partial class FulfillmentDbContext(DbContextOptions<FulfillmentDbContext> options) : DbContext(options)
{
    public DbSet<PackTask> PackTasks => Set<PackTask>();
    public DbSet<PackTaskLine> PackTaskLines => Set<PackTaskLine>();
    public DbSet<PackTaskSerial> PackTaskSerials => Set<PackTaskSerial>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<OrderAccount> OrderAccounts => Set<OrderAccount>();
    public DbSet<OrderLineAccount> OrderLineAccounts => Set<OrderLineAccount>();
    public DbSet<ReturnRequest> ReturnRequests => Set<ReturnRequest>();
    public DbSet<ReturnUnit> ReturnUnits => Set<ReturnUnit>();
    public DbSet<DeliveryFailure> DeliveryFailures => Set<DeliveryFailure>();
    public DbSet<FulfillmentSaga> FulfillmentSagas => Set<FulfillmentSaga>();
    public DbSet<WarrantyCoverage> WarrantyCoverages => Set<WarrantyCoverage>();

    private static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.AddBloomReliabilityTables();

        mb.Entity<PackTask>(e =>
        {
            e.ToTable("pack_tasks", t =>
            {
                t.HasCheckConstraint("ck_pack_tasks_kind", $"kind IN ({In(TaskKinds.All)})");
                t.HasCheckConstraint("ck_pack_tasks_status", $"status IN ({In(TaskStatuses.All)})");
                t.HasCheckConstraint("ck_pack_tasks_shipping_option", $"shipping_option IN ({In(ShippingOptions.All)})");
            });
            e.Property(x => x.ShipTo).HasColumnType("jsonb");
            e.HasIndex(x => x.RefId).IsUnique().HasDatabaseName("ux_pack_tasks_ref_id");
        });

        mb.Entity<PackTaskLine>(e =>
        {
            e.ToTable("pack_task_lines", t => t.HasCheckConstraint("ck_pack_task_lines_kind", $"kind IN ({In(LineKinds.All)})"));
            e.HasKey(x => new { x.TaskId, x.LineNo });
            e.HasOne<PackTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<PackTaskSerial>(e =>
        {
            e.ToTable("pack_task_serials");
            e.HasKey(x => new { x.TaskId, x.Serial });
            e.HasOne<PackTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TaskId, x.Seq }).IsUnique().HasDatabaseName("ux_pack_task_serials_task_seq");
        });

        mb.Entity<Shipment>(e =>
        {
            e.ToTable("shipments", t =>
            {
                t.HasCheckConstraint("ck_shipments_direction", $"direction IN ({In(Directions.All)})");
                t.HasCheckConstraint("ck_shipments_service", $"service IN ({In(ShippingOptions.All)})");
                t.HasCheckConstraint("ck_shipments_status", $"status IN ({In(ShipmentStatuses.All)})");
            });
            e.HasIndex(x => x.ShippoTransactionId).IsUnique().HasDatabaseName("ux_shipments_shippo_transaction_id");
            e.HasIndex(x => x.TrackingNumber).IsUnique().HasDatabaseName("ux_shipments_tracking_number");
            e.HasIndex(x => x.NextAttemptAt).HasFilter("status = 'label_pending'").HasDatabaseName("ix_shipments_label_due");
            e.HasIndex(x => x.PackTaskId).HasDatabaseName("ix_shipments_pack_task");
        });

        mb.Entity<OrderAccount>(e =>
        {
            e.ToTable("order_accounts");
            e.HasKey(x => x.OrderId);
            e.HasIndex(x => x.AccountId).HasDatabaseName("ix_order_accounts_account");
        });

        mb.Entity<OrderLineAccount>(e =>
        {
            e.ToTable("order_line_accounts", t =>
            {
                t.HasCheckConstraint("ck_order_line_accounts_kind", $"kind IN ({In(LineKinds.All)})");
                t.HasCheckConstraint("ck_order_line_accounts_refund_le_paid", "refunded_cents <= paid_cents");
                t.HasCheckConstraint("ck_order_line_accounts_units_le_quantity", "refunded_units <= quantity");
                t.HasCheckConstraint("ck_order_line_accounts_refunded_non_negative", "refunded_units >= 0 AND refunded_cents >= 0");
            });
            e.HasKey(x => new { x.OrderId, x.LineNo });
            e.Property(x => x.RefundedUnits).HasDefaultValue(0);
            e.Property(x => x.RefundedCents).HasDefaultValue(0L);
        });

        mb.Entity<ReturnRequest>(e =>
        {
            e.ToTable("return_requests", t => t.HasCheckConstraint("ck_return_requests_status", $"status IN ({In(ReturnStatuses.All)})"));
            e.HasIndex(x => x.OrderId).HasDatabaseName("ix_return_requests_order");
            e.HasIndex(x => new { x.AccountId, x.CreatedAt }).HasDatabaseName("ix_return_requests_account_created");
            e.HasIndex(x => x.ExpiresAt).HasFilter("status IN ('requested', 'label_issued')").HasDatabaseName("ix_return_requests_expiry");
        });

        mb.Entity<ReturnUnit>(e =>
        {
            e.ToTable("return_units", t =>
            {
                t.HasCheckConstraint("ck_return_units_inspection_result", $"inspection_result IN ({In(InspectionResults.All)})");
                t.HasCheckConstraint("ck_return_units_decision", $"decision IN ({In(Decisions.All)})");
            });
            e.HasKey(x => new { x.ReturnId, x.UnitNo });
            e.HasOne<ReturnRequest>().WithMany().HasForeignKey(x => x.ReturnId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<DeliveryFailure>(e =>
        {
            e.ToTable("delivery_failures", t =>
            {
                t.HasCheckConstraint("ck_delivery_failures_kind", $"kind IN ({In(FailureKinds.All)})");
                t.HasCheckConstraint("ck_delivery_failures_status", $"status IN ({In(FailureStatuses.All)})");
                t.HasCheckConstraint("ck_delivery_failures_previous_status", $"previous_status IN ({In(ShipmentStatuses.All)})");
            });
            e.HasKey(x => x.ShipmentId);
            e.Property(x => x.RtsOutcomes).HasColumnType("jsonb");
        });

        mb.Entity<FulfillmentSaga>(e =>
        {
            e.ToTable("fulfillment_sagas", t => t.HasCheckConstraint("ck_fulfillment_sagas_type", $"type IN ({In(SagaTypes.All)})"));
            e.HasKey(x => new { x.RefId, x.Type });
            e.HasIndex(x => x.RefundId).IsUnique().HasDatabaseName("ux_fulfillment_sagas_refund_id");
        });

        mb.Entity<WarrantyCoverage>(e =>
        {
            e.ToTable("warranty_coverages", t => t.HasCheckConstraint("ck_warranty_coverages_status", $"status IN ({In(CoverageStatuses.All)})"));
            e.HasIndex(x => x.Serial).IsUnique().HasFilter("status IN ('active', 'pending_delivery')").HasDatabaseName("ux_warranty_coverages_serial_open");
            e.HasIndex(x => x.OrderId).HasDatabaseName("ix_warranty_coverages_order");
        });

        // Ids come from the app (UUIDv7); columns are snake_case. The reliability tables name their own columns.
        foreach (var entity in mb.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(PackTask).Namespace))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase().Replace(property.Name, "_").ToLowerInvariant());
            if (entity.FindProperty("Id") is { } id) id.ValueGenerated = ValueGenerated.Never;
        }
    }

    [GeneratedRegex("(?<!^)(?=[A-Z])")]
    private static partial Regex SnakeCase();
}
