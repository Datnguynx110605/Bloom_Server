using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Shared;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;

namespace Bloom.Inventory.Domain;

/// <summary>Staff-action audit (spec §7): an outbox event in the caller's transaction, which identity's audit consumer stores.</summary>
public sealed class Audit(OutboxWriter outbox, BloomOptions options)
{
    /// <param name="action">Full action name, for example <c>inventory.goods-received</c>.</param>
    public void Record(Guid actorId, string actorRole, string action, string targetType, string targetId, object? details = null) =>
        outbox.PublishEvent(IdentityMessages.StaffActionRecorded, new StaffActionRecorded(
            actorId, actorRole, options.ServiceName, action, targetType, targetId,
            JsonSerializer.SerializeToElement(details ?? new { }, JsonSerializerOptions.Web)));
}
