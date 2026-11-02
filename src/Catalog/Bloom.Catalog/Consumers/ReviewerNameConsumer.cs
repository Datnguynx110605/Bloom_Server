using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Bloom.Identity.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Consumers;

/// <summary>08-catalog §5.2: keeps <c>reviewer_names</c> in step with registration and contact changes. Old reviews keep the name they were written with (§8).</summary>
public sealed class ReviewerNameConsumer(CatalogDbContext db) : IMessageHandler
{
    /// <summary>One binding per queue: <c>catalog.customer-registered</c> takes [0], <c>catalog.contact-changed</c> takes [1] (plan R25).</summary>
    public static ConsumerBinding[] Bindings { get; } =
        [ConsumerBinding.Event("identity", IdentityMessages.CustomerRegistered), ConsumerBinding.Event("identity", IdentityMessages.ContactChanged)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        // Both payloads have the same shape (AccountId, Email, Name).
        var (accountId, name) = envelope.MessageType switch
        {
            IdentityMessages.CustomerRegistered => Pick(envelope.GetPayload<CustomerRegisteredV1>()),
            IdentityMessages.ContactChanged => Pick(envelope.GetPayload<ContactChangedV1>()),
            _ => throw new PermanentFailureException($"Unexpected message type {envelope.MessageType}."),
        };
        var display = ReviewerNames.Derive(name);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO reviewer_names (account_id, display_name) VALUES ({accountId}, {display}) ON CONFLICT (account_id) DO UPDATE SET display_name = EXCLUDED.display_name", ct);
    }

    private static (Guid AccountId, string Name) Pick(CustomerRegisteredV1 p) => (p.AccountId, p.Name);

    private static (Guid AccountId, string Name) Pick(ContactChangedV1 p) => (p.AccountId, p.Name);
}
