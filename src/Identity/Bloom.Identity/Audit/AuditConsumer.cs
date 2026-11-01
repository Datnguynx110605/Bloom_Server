using Bloom.Identity.Contracts;
using Bloom.Identity.Data;
using Bloom.Identity.Observability;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Audit;

/// <summary>Other services' staff actions (DLQ replay or discard) into <c>audit_log</c> (06-identity §6.10, LLD-ADR-009); runs in the consumer host's inbox transaction.</summary>
public sealed class AuditConsumer(IdentityDbContext db, IdentityMetrics metrics) : IMessageHandler
{
    /// <summary>Every service hosts the DLQ admin that publishes the audit message.</summary>
    public static readonly string[] Producers = ["catalog", "inventory", "launch", "order", "payment", "fulfillment", "identity", "notification"];

    public static ConsumerBinding[] Bindings { get; } =
        [.. Producers.Select(p => ConsumerBinding.Event(p, IdentityMessages.StaffActionRecorded))];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var p = envelope.GetPayload<StaffActionRecorded>(); // before any side effect: an invalid payload dead-letters at once
        var occurredAt = new DateTimeOffset(envelope.OccurredAt.ToUniversalTime());
        var details = p.Details.GetRawText();
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO audit_log (id, occurred_at, actor_id, actor_role, service, action, target_type, target_id, details)
            VALUES ({envelope.MessageId}, {occurredAt}, {p.ActorId}, {p.ActorRole}, {p.Service}, {p.Action}, {p.TargetType}, {p.TargetId}, {details}::jsonb)
            ON CONFLICT (id) DO NOTHING
            """, ct);
        if (inserted == 1) metrics.AuditEntry(p.Service);
    }
}
