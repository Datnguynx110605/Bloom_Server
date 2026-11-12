using Microsoft.EntityFrameworkCore;

namespace Bloom.Shared.Persistence;

public static class ModelBuilderExtensions
{
    private const string Timestamptz = "timestamp with time zone";

    /// <summary>Adds the three reliability tables of LLD 00-reliability §4.1-§4.3 to a service's model.</summary>
    public static ModelBuilder AddBloomReliabilityTables(this ModelBuilder mb)
    {
        mb.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages", t =>
            {
                t.HasCheckConstraint("ck_outbox_messages_kind", "kind IN ('event', 'command')");
                t.HasCheckConstraint("ck_outbox_messages_status", "status IN ('pending', 'sent')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.MessageType).HasColumnName("message_type");
            e.Property(x => x.Kind).HasColumnName("kind");
            e.Property(x => x.Exchange).HasColumnName("exchange");
            e.Property(x => x.RoutingKey).HasColumnName("routing_key");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.Headers).HasColumnName("headers").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType(Timestamptz).HasDefaultValueSql("now()");
            e.Property(x => x.SentAt).HasColumnName("sent_at").HasColumnType(Timestamptz);
            e.HasIndex(x => x.Id, "ix_outbox_messages_pending").HasFilter("status = 'pending'");
            e.HasIndex(x => x.SentAt, "ix_outbox_messages_sent_at").HasFilter("status = 'sent'");
        });

        mb.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
            e.Property(x => x.Consumer).HasColumnName("consumer");
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.MessageType).HasColumnName("message_type");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at").HasColumnType(Timestamptz).HasDefaultValueSql("now()");
            e.HasIndex(x => x.ProcessedAt, "ix_inbox_messages_processed_at");
        });

        mb.Entity<IdempotencyKey>(e =>
        {
            e.ToTable("idempotency_keys", t =>
                t.HasCheckConstraint("ck_idempotency_keys_status", "status IN ('in_progress', 'completed')"));
            e.HasKey(x => new { x.UserId, x.Key });
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.Operation).HasColumnName("operation");
            e.Property(x => x.RequestHash).HasColumnName("request_hash");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until").HasColumnType(Timestamptz);
            e.Property(x => x.ResponseStatus).HasColumnName("response_status");
            e.Property(x => x.ResponseBody).HasColumnName("response_body");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType(Timestamptz).HasDefaultValueSql("now()");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType(Timestamptz);
            e.HasIndex(x => x.CreatedAt, "ix_idempotency_keys_created_at");
        });

        return mb;
    }
}
