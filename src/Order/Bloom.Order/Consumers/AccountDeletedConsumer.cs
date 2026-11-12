using Bloom.Identity.Contracts;
using Bloom.Order.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Consumers;

/// <summary>
/// <c>identity.account-deleted.v1</c> (03-order §6.9, ACC-R06): on every order of the account, <c>ship_to</c> name, lines and phone become <c>"[deleted]"</c> (one entry per line);
/// city, state, ZIP, amounts, SKUs, dates and <c>account_id</c> stay. The cart and its lines are deleted. Idempotent, and an unknown account matches no row.
/// </summary>
public sealed class AccountDeletedConsumer(OrderDbContext db) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("identity", IdentityMessages.AccountDeleted)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var accountId = envelope.GetPayload<AccountDeletedV1>().AccountId;
        // ship_to is jsonb written with Web options, so its keys are camelCase.
        await db.Database.ExecuteSqlAsync($$"""
            UPDATE orders
            SET ship_to = jsonb_set(jsonb_set(jsonb_set(ship_to, '{name}', '"[deleted]"'), '{phone}', '"[deleted]"'), '{lines}',
                (SELECT coalesce(jsonb_agg('"[deleted]"'::jsonb), '[]'::jsonb) FROM jsonb_array_elements(ship_to -> 'lines')))
            WHERE account_id = {{accountId}}
            """, ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM cart_lines WHERE account_id = {accountId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM carts WHERE account_id = {accountId}", ct);
    }
}
