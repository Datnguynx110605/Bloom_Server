using System.Text.Json;
using System.Text.RegularExpressions;
using Bloom.Payment.Contracts;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bloom.Payment.Data;

public sealed partial class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentRow> Payments => Set<PaymentRow>();
    public DbSet<RefundRow> Refunds => Set<RefundRow>();
    public DbSet<ProviderEvent> ProviderEvents => Set<ProviderEvent>();
    public DbSet<DisputeRow> Disputes => Set<DisputeRow>();
    public DbSet<JobRunRow> JobRuns => Set<JobRunRow>();

    private static string In(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"'{v}'"));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.AddBloomReliabilityTables();

        mb.Entity<PaymentRow>(e =>
        {
            e.ToTable("payments", t =>
            {
                t.HasCheckConstraint("ck_payments_amount_positive", "amount_cents > 0");
                t.HasCheckConstraint("ck_payments_refunded_non_negative", "refunded_cents >= 0");
                t.HasCheckConstraint("ck_payments_refunded_le_amount", "refunded_cents <= amount_cents");
                t.HasCheckConstraint("ck_payments_status", $"status IN ({In(PaymentStatuses.All)})");
                t.HasCheckConstraint("ck_payments_currency", "currency = 'usd'");
            });
            e.Property(x => x.CancelRequested).HasDefaultValue(false);
            e.Property(x => x.RefundedCents).HasDefaultValue(0L);
            e.Property(x => x.Version).HasDefaultValue(0L);
            e.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("ux_payments_order_id");
            e.HasIndex(x => x.StripeSessionId).IsUnique().HasDatabaseName("ux_payments_stripe_session_id");
            e.HasIndex(x => x.StripePaymentIntentId).IsUnique().HasDatabaseName("ux_payments_stripe_payment_intent_id");
            e.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_payments_status_created_at");
        });

        mb.Entity<RefundRow>(e =>
        {
            e.ToTable("refunds", t =>
            {
                t.HasCheckConstraint("ck_refunds_amount_positive", "amount_cents > 0");
                t.HasCheckConstraint("ck_refunds_status", $"status IN ({In(RefundStatuses.All)})");
                t.HasCheckConstraint("ck_refunds_attempts_non_negative", "attempts >= 0");
            });
            e.Property(x => x.Attempts).HasDefaultValue(0);
            e.Property(x => x.Lines).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Web),
                s => JsonSerializer.Deserialize<List<RefundLineV1>>(s, JsonSerializerOptions.Web),
                // The lines are records, so a shallow copy and a sequence compare are value-correct.
                new ValueComparer<List<RefundLineV1>?>(
                    (a, b) => a == b || (a != null && b != null && a.SequenceEqual(b)),
                    v => v == null ? 0 : v.Aggregate(0, (h, x) => HashCode.Combine(h, x)),
                    v => v == null ? null : v.ToList()));
            e.HasOne<PaymentRow>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.PaymentId).HasDatabaseName("ix_refunds_payment_id");
            e.HasIndex(x => x.StripeRefundId).IsUnique().HasDatabaseName("ux_refunds_stripe_refund_id");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_refunds_status_next_attempt_at");
        });

        mb.Entity<ProviderEvent>(e =>
        {
            e.ToTable("provider_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.ReceivedAt).HasDatabaseName("ix_provider_events_received_at");
        });

        mb.Entity<DisputeRow>(e =>
        {
            e.ToTable("disputes");
            e.HasOne<PaymentRow>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.PaymentId).HasDatabaseName("ix_disputes_payment_id");
            e.HasIndex(x => x.StripeDisputeId).IsUnique().HasDatabaseName("ux_disputes_stripe_dispute_id");
        });

        mb.Entity<JobRunRow>(e =>
        {
            e.ToTable("job_runs");
            e.HasKey(x => x.Name);
            e.HasData(new JobRunRow { Name = "nightly_compare", LastRunDate = new DateOnly(2000, 1, 1) }); // R6: older than any real night
        });

        // Ids come from the app (UUIDv7); columns are snake_case. The reliability tables name their own columns.
        foreach (var entity in mb.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(PaymentRow).Namespace))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase().Replace(property.Name, "_").ToLowerInvariant());
            if (entity.FindProperty("Id") is { } id && id.ClrType == typeof(Guid)) id.ValueGenerated = ValueGenerated.Never;
        }
    }

    [GeneratedRegex("(?<!^)(?=[A-Z])")]
    private static partial Regex SnakeCase();
}
