using Bloom.Fulfillment.Data;
using Bloom.Identity.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Consumers;

/// <summary>
/// <c>identity.account-deleted.v1</c> (05-fulfillment §9, plan R20): on every pack task of the account's orders, <c>ship_to</c> name, lines and phone become <c>"[deleted]"</c>;
/// city, state and ZIP stay, and so do tracking numbers and amounts. Idempotent, and an unknown account matches no row. The task's <c>version</c> is not bumped: a packer in flight keeps working.
/// </summary>
public sealed class AccountDeletedConsumer(FulfillmentDbContext db) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("identity", IdentityMessages.AccountDeleted)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var accountId = envelope.GetPayload<AccountDeletedV1>().AccountId;
        await db.Database.ExecuteSqlAsync($$"""
            UPDATE pack_tasks
            SET ship_to = jsonb_set(jsonb_set(jsonb_set(ship_to, '{name}', '"[deleted]"'), '{phone}', '"[deleted]"'), '{lines}',
                (SELECT coalesce(jsonb_agg('"[deleted]"'::jsonb), '[]'::jsonb) FROM jsonb_array_elements(ship_to -> 'lines')))
            WHERE ship_to IS NOT NULL AND order_id IN (SELECT order_id FROM order_accounts WHERE account_id = {{accountId}})
            """, ct);
    }
}
