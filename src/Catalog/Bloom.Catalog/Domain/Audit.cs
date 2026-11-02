using System.Text.Json;
using Bloom.Catalog.Context;
using Bloom.Identity.Contracts;
using Bloom.Shared;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;

namespace Bloom.Catalog.Domain;

/// <summary>Staff-action audit (plan R13): an outbox event in the caller's transaction, which identity's audit consumer stores.</summary>
public sealed class Audit(OutboxWriter outbox, BloomOptions options)
{
    /// <param name="action">Full action name, for example <c>catalog.category-created</c>.</param>
    /// <param name="details"><c>{before, after}</c> for updates, <c>{reason}</c> for hide / unhide, otherwise none (<c>{}</c>).</param>
    public void Record(Caller caller, string action, string targetType, Guid targetId, object? details = null) =>
        outbox.PublishEvent(IdentityMessages.StaffActionRecorded, new StaffActionRecorded(
            caller.UserId, caller.Role!, options.ServiceName, action, targetType, targetId.ToString(),
            JsonSerializer.SerializeToElement(details ?? new { }, JsonSerializerOptions.Web)));
}
