using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Launch.Context;
using Bloom.Shared;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;

namespace Bloom.Launch.Domain;

/// <summary>Staff-action audit (plan R13, as catalog's): an outbox event in the caller's transaction, which identity's audit consumer stores. The target is always a launch.</summary>
public sealed class Audit(OutboxWriter outbox, BloomOptions options)
{
    /// <param name="action">Full action name, for example <c>launch.sku-added</c>.</param>
    /// <param name="details"><c>{before, after}</c> for changes, the added row for adds, otherwise none (<c>{}</c>).</param>
    public void Record(Caller caller, string action, Guid launchId, object? details = null) =>
        outbox.PublishEvent(IdentityMessages.StaffActionRecorded, new StaffActionRecorded(
            caller.UserId, caller.Role!, options.ServiceName, action, "launch", launchId.ToString("D"),
            JsonSerializer.SerializeToElement(details ?? new { }, JsonSerializerOptions.Web)));
}
