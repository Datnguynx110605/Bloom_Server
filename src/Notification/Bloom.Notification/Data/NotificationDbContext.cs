using System.Text.RegularExpressions;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Bloom.Notification.Data;

public sealed partial class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options), IAfterCommitContext
{
    public DbSet<ContactRow> Contacts => Set<ContactRow>();
    public DbSet<OrderRefRow> OrderRefs => Set<OrderRefRow>();
    public DbSet<NotificationRow> Notifications => Set<NotificationRow>();
    public DbSet<EmailJobRow> EmailJobs => Set<EmailJobRow>();

    /// <summary>Work that must run only after the transaction commits (pushes, disconnects, metrics); dropped on a rollback (plan N7).</summary>
    public AfterCommitActions AfterCommit { get; } = new();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.AddBloomReliabilityTables();

        mb.Entity<ContactRow>(e =>
        {
            e.ToTable("contacts");
            e.HasKey(x => x.AccountId);
        });

        mb.Entity<OrderRefRow>(e =>
        {
            e.ToTable("order_refs");
            e.HasKey(x => x.OrderId);
        });

        mb.Entity<NotificationRow>(e =>
        {
            e.ToTable("notifications");
            e.HasIndex(x => new { x.SourceMessageId, x.Type }).IsUnique().HasDatabaseName("ux_notifications_source_message_id_type");
            e.HasIndex(x => new { x.AccountId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_notifications_account_id_created_at");
            e.HasIndex(x => x.AccountId).HasFilter("read_at IS NULL").HasDatabaseName("ix_notifications_account_id_unread");
        });

        mb.Entity<EmailJobRow>(e =>
        {
            e.ToTable("email_jobs", t =>
            {
                t.HasCheckConstraint("ck_email_jobs_status", $"status IN ({string.Join(", ", EmailStatuses.All.Select(s => $"'{s}'"))})");
                t.HasCheckConstraint("ck_email_jobs_attempts_non_negative", "attempts >= 0");
            });
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasIndex(x => new { x.SourceMessageId, x.Template }).IsUnique().HasDatabaseName("ux_email_jobs_source_message_id_template");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_email_jobs_status_next_attempt_at");
        });

        // Ids come from the app (UUIDv7); columns are snake_case. The reliability tables name their own columns.
        foreach (var entity in mb.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(NotificationRow).Namespace))
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(SnakeCase().Replace(property.Name, "_").ToLowerInvariant());
            if (entity.FindProperty("Id") is { } id && id.ClrType == typeof(Guid)) id.ValueGenerated = ValueGenerated.Never;
        }
    }

    [GeneratedRegex("(?<!^)(?=[A-Z])")]
    private static partial Regex SnakeCase();
}
