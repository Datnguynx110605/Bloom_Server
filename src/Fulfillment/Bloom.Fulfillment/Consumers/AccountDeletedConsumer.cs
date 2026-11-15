using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Identity.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Consumers;

/// <summary>
/// <c>identity.account-deleted.v1</c> (05-fulfillment §9, plan R20): on every pack task of the account's orders, <c>ship_to</c> name, lines and phone become <c>"[deleted]"</c>;
/// city, state and ZIP stay, and so do tracking numbers and amounts. Idempotent, and an unknown account matches no row. The task's <c>version</c> is not bumped: a packer in flight keeps working.
/// D31 (05-fulfillment §6.7): the account's return requests still in <c>requested</c> become <c>expired</c> (their unbought label voided, so the label worker never picks them up) and
/// <c>return-closed</c> (reason <c>expired</c>) is published for each. Lock order as the expiry sweep: shipments, then return_requests.
/// </summary>
public sealed class AccountDeletedConsumer(FulfillmentDbContext db, OutboxWriter outbox) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("identity", IdentityMessages.AccountDeleted)];

    private sealed class Closed
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }
    }

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var accountId = envelope.GetPayload<AccountDeletedV1>().AccountId;
        await db.Database.ExecuteSqlAsync($$"""
            UPDATE pack_tasks
            SET ship_to = jsonb_set(jsonb_set(jsonb_set(ship_to, '{name}', '"[deleted]"'), '{phone}', '"[deleted]"'), '{lines}',
                (SELECT coalesce(jsonb_agg('"[deleted]"'::jsonb), '[]'::jsonb) FROM jsonb_array_elements(ship_to -> 'lines')))
            WHERE ship_to IS NOT NULL AND order_id IN (SELECT order_id FROM order_accounts WHERE account_id = {{accountId}})
            """, ct);

        await db.Database.ExecuteSqlAsync($"""
            SELECT 1 FROM shipments
            WHERE direction = {Directions.Return}
              AND return_request_id IN (SELECT id FROM return_requests WHERE account_id = {accountId} AND status = {ReturnStatuses.Requested})
            ORDER BY id FOR UPDATE
            """, ct); // waits for a label worker that is buying one of them; its request is then label_issued and left alone
        var closed = await db.Database.SqlQuery<Closed>($"""
            UPDATE return_requests SET status = {ReturnStatuses.Expired}, version = version + 1, updated_at = now()
            WHERE account_id = {accountId} AND status = {ReturnStatuses.Requested}
            RETURNING id AS "Id", order_id AS "OrderId"
            """).ToListAsync(ct);
        foreach (var r in closed)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE shipments SET status = {ShipmentStatuses.Voided}, version = version + 1
                WHERE return_request_id = {r.Id} AND direction = {Directions.Return} AND status = {ShipmentStatuses.LabelPending}
                """, ct);
            outbox.PublishEvent(FulfillmentMessages.ReturnClosed, new ReturnClosedV1(r.OrderId, r.Id, "expired"));
        }
    }
}
