using System.Text.Json;
using Bloom.Identity.Context;
using Bloom.Identity.Data;
using Bloom.Identity.Observability;

namespace Bloom.Identity.Audit;

/// <summary>Identity's own staff-action rows in <c>audit_log</c> (06-identity §6.9, plan I26); written in the caller's transaction.</summary>
public sealed class AuditWriter(IdentityDbContext db, TimeProvider time, IdentityMetrics metrics)
{
    /// <summary>Adds the row and counts it; the caller saves. <paramref name="actor"/> is an admin (staff, with a role).</summary>
    public void Add(TrustedCaller actor, string action, string targetId, object details)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = time.GetUtcNow(),
            ActorId = actor.UserId,
            ActorRole = actor.Role!,
            Service = "identity",
            Action = action,
            TargetType = "staff_account",
            TargetId = targetId,
            Details = JsonSerializer.Serialize(details),
        });
        metrics.AuditEntry("identity");
    }
}
