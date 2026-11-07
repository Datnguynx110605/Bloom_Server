using System.Text.Json;
using System.Text.RegularExpressions;
using Bloom.Order.Contracts;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bloom.Order.Data;

public sealed partial class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<OrderRow> Orders => Set<OrderRow>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<OrderSaga> OrderSagas => Set<OrderSaga>();
    public DbSet<CheckoutKey> CheckoutKeys => Set<CheckoutKey>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartLine> CartLines => Set<CartLine>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<CouponScopeItem> CouponScopeItems => Set<CouponScopeItem>();
    public DbSet<CouponCustomerUsage> CouponCustomerUsages => Set<CouponCustomerUsage>();
    public DbSet<CouponUse> CouponUses => Set<CouponUse>();
    public DbSet<CheckoutSettings> CheckoutSettings => Set<CheckoutSettings>();

    private static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.AddBloomReliabilityTables();

        mb.Entity<OrderRow>(e =>
        {
            e.ToTable("orders", t =>
            {
                t.HasCheckConstraint("ck_orders_kind", $"kind IN ({In(OrderKinds.All)})");
                t.HasCheckConstraint("ck_orders_status", $"status IN ({In(OrderStatuses.All)})");
                t.HasCheckConstraint("ck_orders_cancel_reason", $"cancel_reason IN ({In(CancelReasons.All)})");
                t.HasCheckConstraint("ck_orders_shipping_option", $"shipping_option IN ({In(ShippingOptions.All)})");
                t.HasCheckConstraint("ck_orders_amounts_non_negative",
                    "subtotal_cents >= 0 AND discount_cents >= 0 AND tax_cents >= 0 AND shipping_cents >= 0 AND total_cents >= 0");
                t.HasCheckConstraint("ck_orders_open_returns_non_negative", "open_returns >= 0");
            });
            e.Property(x => x.StockAllocated).HasDefaultValue(false);
            e.Property(x => x.LaunchReleased).HasDefaultValue(false);
            e.Property(x => x.HandedToFulfillment).HasDefaultValue(false);
            e.Property(x => x.OpenReturns).HasDefaultValue(0);
            e.Property(x => x.Version).HasDefaultValue(0L);
            e.Property(x => x.ShipTo).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Web),
                s => JsonSerializer.Deserialize<ShipToV1>(s, JsonSerializerOptions.Web)!);
            e.HasIndex(x => x.Number).IsUnique().HasDatabaseName("ux_orders_number");
            e.HasIndex(x => new { x.Status, x.DeadlineAt }).HasFilter("status IN ('creating', 'pending_payment')").HasDatabaseName("ix_orders_sweeps");
            e.HasIndex(x => new { x.AccountId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_orders_account_created");
            e.HasIndex(x => new { x.LaunchId, x.Status }).HasFilter("kind = 'launch'").HasDatabaseName("ix_orders_launch_status");
        });

        mb.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines", t =>
            {
                t.HasCheckConstraint("ck_order_lines_kind", $"kind IN ({In(LineKinds.All)})");
                t.HasCheckConstraint("ck_order_lines_quantity_positive", "quantity > 0");
                t.HasCheckConstraint("ck_order_lines_refund_le_paid", "refunded_cents <= paid_cents");
                t.HasCheckConstraint("ck_order_lines_returned_qty_in_range", "returned_qty BETWEEN 0 AND quantity");
                t.HasCheckConstraint("ck_order_lines_refunded_non_negative", "refunded_cents >= 0");
            });
            e.HasKey(x => new { x.OrderId, x.LineNo });
            e.Property(x => x.ReturnedQty).HasDefaultValue(0);
            e.Property(x => x.RefundedCents).HasDefaultValue(0L);
            e.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<OrderStatusHistory>(e =>
        {
            e.ToTable("order_status_history", t =>
            {
                t.HasCheckConstraint("ck_order_status_history_from_status", $"from_status IN ({In(OrderStatuses.All)})");
                t.HasCheckConstraint("ck_order_status_history_to_status", $"to_status IN ({In(OrderStatuses.All)})");
                t.HasCheckConstraint("ck_order_status_history_actor_type", $"actor_type IN ({In(ActorTypes.All)})");
            });
            e.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrderId, x.At }).HasDatabaseName("ix_order_status_history_order_at");
        });

        mb.Entity<OrderSaga>(e =>
        {
            e.ToTable("order_sagas", t =>
            {
                t.HasCheckConstraint("ck_order_sagas_type", $"type IN ({In(SagaTypes.All)})");
                t.HasCheckConstraint("ck_order_sagas_pending_steps", $"pending_steps <@ ARRAY[{In(SagaSteps.All)}]::text[]");
            });
            e.HasKey(x => new { x.OrderId, x.Type });
            e.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<CheckoutKey>(e =>
        {
            e.ToTable("checkout_keys");
            e.HasKey(x => new { x.AccountId, x.IdempotencyKey });
            e.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("ux_checkout_keys_order_id");
        });

        mb.Entity<Cart>(e =>
        {
            e.ToTable("carts");
            e.HasKey(x => x.AccountId);
            e.Property(x => x.Version).HasDefaultValue(0L);
        });

        mb.Entity<CartLine>(e =>
        {
            e.ToTable("cart_lines", t =>
            {
                t.HasCheckConstraint("ck_cart_lines_kind", $"kind IN ({In(LineKinds.All)})");
                t.HasCheckConstraint("ck_cart_lines_quantity_positive", "quantity > 0");
            });
            e.HasKey(x => new { x.AccountId, x.LineNo });
            e.HasOne<Cart>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<Coupon>(e =>
        {
            e.ToTable("coupons", t =>
            {
                t.HasCheckConstraint("ck_coupons_type", $"type IN ({In(CouponTypes.All)})");
                t.HasCheckConstraint("ck_coupons_scope", $"scope IN ({In(CouponScopes.All)})");
                t.HasCheckConstraint("ck_coupons_value_by_type",
                    "(type = 'percent' AND value BETWEEN 1 AND 100) OR (type = 'fixed' AND value > 0)");
                t.HasCheckConstraint("ck_coupons_uses_le_total_limit", "total_limit IS NULL OR uses_count <= total_limit");
                t.HasCheckConstraint("ck_coupons_uses_non_negative", "uses_count >= 0");
            });
            e.Property(x => x.UsesCount).HasDefaultValue(0);
            e.Property(x => x.Version).HasDefaultValue(0L);
            e.HasIndex(x => x.CodeNormalized).IsUnique().HasDatabaseName("ux_coupons_code_normalized");
        });

        mb.Entity<CouponScopeItem>(e =>
        {
            e.ToTable("coupon_scope_items", t => t.HasCheckConstraint("ck_coupon_scope_items_target_type", $"target_type IN ({In(TargetTypes.All)})"));
            e.HasKey(x => new { x.CouponId, x.TargetType, x.TargetId });
            e.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<CouponCustomerUsage>(e =>
        {
            e.ToTable("coupon_customer_usage", t => t.HasCheckConstraint("ck_coupon_customer_usage_uses_non_negative", "uses_count >= 0"));
            e.HasKey(x => new { x.CouponId, x.AccountId });
            e.Property(x => x.UsesCount).HasDefaultValue(0);
            e.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<CouponUse>(e =>
        {
            e.ToTable("coupon_uses", t => t.HasCheckConstraint("ck_coupon_uses_status", $"status IN ({In(CouponUseStatuses.All)})"));
            e.HasKey(x => x.OrderId);
            e.HasOne<OrderRow>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Coupon>().WithMany().HasForeignKey(x => x.CouponId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<CheckoutSettings>(e =>
        {
            e.ToTable("checkout_settings", t =>
            {
                t.HasCheckConstraint("ck_checkout_settings_single_row", "id = 1");
                t.HasCheckConstraint("ck_checkout_settings_express_fee_non_negative", "express_fee_cents >= 0");
            });
            e.Property(x => x.Id).ValueGeneratedNever();
            // The migration time is a literal: seed data must be constant (spec §4).
            e.HasData(new CheckoutSettings { Id = 1, ExpressFeeCents = 1500, Version = 0, UpdatedAt = new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero) });
        });

        // Ids come from the app (UUIDv7); columns are snake_case. The reliability tables name their own columns.
        foreach (var entity in mb.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(OrderRow).Namespace))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase().Replace(property.Name, "_").ToLowerInvariant());
            if (entity.FindProperty("Id") is { } id && id.ClrType == typeof(Guid)) id.ValueGenerated = ValueGenerated.Never;
        }
    }

    [GeneratedRegex("(?<!^)(?=[A-Z])")]
    private static partial Regex SnakeCase();
}
