using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Bloom.Identity.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Consumers;

/// <summary>08-catalog §6.6, REV-R08: reviews stay (visible and counted) under "Former customer"; eligibility and name rows go.</summary>
public sealed class AccountDeletedConsumer(CatalogDbContext db) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("identity", IdentityMessages.AccountDeleted)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var accountId = envelope.GetPayload<AccountDeletedV1>().AccountId;
        await db.Database.ExecuteSqlAsync($"UPDATE reviews SET account_id = NULL, author_display = {ReviewerNames.FormerCustomer} WHERE account_id = {accountId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM review_eligibility WHERE account_id = {accountId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM reviewer_names WHERE account_id = {accountId}", ct);
    }
}
