using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Identity.Contracts;
using Bloom.Notification.Data;
using Bloom.Notification.Observability;
using Bloom.Notification.Sockets;
using Bloom.Notification.Templates;
using Bloom.Order.Contracts;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Bloom.Notification.Consumers;

/// <summary>
/// Turns the 17 consumed events into contacts, order refs, in-app rows, email jobs and disconnects (09-notification §5.3, §6.1, §6.5; plan N6).
/// One class for every queue: the message type picks the effect. It runs inside <c>ConsumerHost</c>'s transaction, so pushes, disconnects and
/// <c>notifications_created_total</c> are queued on <see cref="NotificationDbContext.AfterCommit"/> (plan N7).
/// </summary>
public sealed class EventConsumer(
    NotificationDbContext db, TemplateRenderer templates, NotificationMetrics metrics,
    IConnectionMultiplexer redis, IHubContext<NotificationHub> hub) : IMessageHandler
{
    public static IReadOnlyList<(string Handler, ConsumerBinding Binding)> Handlers { get; } =
    [
        ("identity-verification-code", ConsumerBinding.Event("identity", IdentityMessages.VerificationCode)),
        ("identity-staff-invited", ConsumerBinding.Event("identity", IdentityMessages.StaffInvited)),
        ("identity-customer-registered", ConsumerBinding.Event("identity", IdentityMessages.CustomerRegistered)),
        ("identity-contact-changed", ConsumerBinding.Event("identity", IdentityMessages.ContactChanged)),
        ("identity-account-deleted", ConsumerBinding.Event("identity", IdentityMessages.AccountDeleted)),
        ("identity-tokens-revoked", ConsumerBinding.Event("identity", IdentityMessages.TokensRevoked)),
        ("order-paid", ConsumerBinding.Event("order", OrderMessages.Paid)),
        ("order-cancelled", ConsumerBinding.Event("order", OrderMessages.Cancelled)),
        ("order-preorder-delayed", ConsumerBinding.Event("order", OrderMessages.PreorderDelayed)),
        ("fulfillment-shipment-shipped", ConsumerBinding.Event("fulfillment", FulfillmentMessages.ShipmentShipped)),
        ("fulfillment-shipment-delivered", ConsumerBinding.Event("fulfillment", FulfillmentMessages.ShipmentDelivered)),
        ("fulfillment-delivery-failed", ConsumerBinding.Event("fulfillment", FulfillmentMessages.DeliveryFailed)),
        ("fulfillment-return-label-issued", ConsumerBinding.Event("fulfillment", FulfillmentMessages.ReturnLabelIssued)),
        ("fulfillment-return-received", ConsumerBinding.Event("fulfillment", FulfillmentMessages.ReturnReceived)),
        ("fulfillment-return-decided", ConsumerBinding.Event("fulfillment", FulfillmentMessages.ReturnDecided)),
        ("fulfillment-return-closed", ConsumerBinding.Event("fulfillment", FulfillmentMessages.ReturnClosed)),
        ("payment-refund-completed", ConsumerBinding.Event("payment", PaymentMessages.RefundCompleted)),
    ];

    public async Task HandleAsync(Envelope e, CancellationToken ct)
    {
        // GetPayload comes first in every case: an invalid payload dead-letters at once, with nothing written.
        switch (e.MessageType)
        {
            case IdentityMessages.VerificationCode:
                var code = e.GetPayload<VerificationCodeV1>();
                // The code stays encrypted exactly as received until the sender decrypts it (NR-03).
                await EnqueueEmail(e, NotificationTypes.AccountCode, null, code.Email,
                    new() { ["purpose"] = code.Purpose, ["encryptedCode"] = code.EncryptedCode, ["expiresAt"] = code.ExpiresAt.ToString("O") }, ct);
                break;
            case IdentityMessages.StaffInvited:
                var invited = e.GetPayload<StaffInvitedV1>();
                await EnqueueEmail(e, NotificationTypes.StaffInvitation, null, invited.Email, new() { ["role"] = invited.Role, ["inviteUrl"] = invited.InviteUrl }, ct);
                break;
            case IdentityMessages.CustomerRegistered:
                var registered = e.GetPayload<CustomerRegisteredV1>();
                await UpsertContact(e, registered.AccountId, registered.Email, registered.Name, ct);
                break;
            case IdentityMessages.ContactChanged:
                var changed = e.GetPayload<ContactChangedV1>();
                await UpsertContact(e, changed.AccountId, changed.Email, changed.Name, ct);
                break;
            case IdentityMessages.AccountDeleted: await AccountDeleted(e.GetPayload<AccountDeletedV1>().AccountId, ct); break;
            case IdentityMessages.TokensRevoked: Disconnect(e.GetPayload<TokensRevokedV1>().AccountId); break;
            case OrderMessages.Paid:
                var paid = e.GetPayload<OrderPaidV1>();
                await InsertOrderRef(paid.OrderId, paid.AccountId, paid.OrderNumber, ct);
                await Notify(e, NotificationTypes.OrderConfirmed, paid.AccountId, paid.OrderNumber, [], ct);
                break;
            case OrderMessages.Cancelled:
                var cancelled = e.GetPayload<OrderCancelledV1>();
                await InsertOrderRef(cancelled.OrderId, cancelled.AccountId, cancelled.OrderNumber, ct);
                await Notify(e, NotificationTypes.OrderCancelled, cancelled.AccountId, cancelled.OrderNumber, new() { ["reason"] = cancelled.Reason }, ct);
                break;
            case OrderMessages.PreorderDelayed:
                var delayed = e.GetPayload<OrderPreorderDelayedV1>();
                await Notify(e, NotificationTypes.OrderPreorderDelayed, delayed.AccountId, delayed.OrderNumber, [], ct);
                break;
            case FulfillmentMessages.ShipmentShipped:
                var shipped = e.GetPayload<ShipmentShippedV1>();
                await ViaOrderRef(e, shipped.OrderId, NotificationTypes.OrderShipped, new() { ["trackingNumber"] = shipped.TrackingNumber }, ct);
                break;
            case FulfillmentMessages.ShipmentDelivered:
                await ViaOrderRef(e, e.GetPayload<ShipmentDeliveredV1>().OrderId, NotificationTypes.OrderDelivered, [], ct);
                break;
            case FulfillmentMessages.DeliveryFailed:
                var failed = e.GetPayload<DeliveryFailedV1>();
                await ViaOrderRef(e, failed.OrderId, NotificationTypes.OrderDeliveryFailed, new() { ["kind"] = failed.Kind }, ct);
                break;
            case FulfillmentMessages.ReturnLabelIssued:
                var label = e.GetPayload<ReturnLabelIssuedV1>();
                await ViaOrderRef(e, label.OrderId, NotificationTypes.ReturnLabelIssued, new() { ["labelUrl"] = label.LabelUrl }, ct);
                break;
            case FulfillmentMessages.ReturnReceived:
                await ViaOrderRef(e, e.GetPayload<ReturnReceivedV1>().OrderId, NotificationTypes.ReturnReceived, [], ct);
                break;
            case FulfillmentMessages.ReturnDecided:
                var decided = e.GetPayload<ReturnDecidedV1>();
                await ViaOrderRef(e, decided.OrderId, NotificationTypes.ReturnDecided, new() { ["refundAmount"] = Money.Format(decided.RefundCents) }, ct);
                break;
            case FulfillmentMessages.ReturnClosed:
                var closed = e.GetPayload<ReturnClosedV1>();
                if (closed.Reason == "expired") await ViaOrderRef(e, closed.OrderId, NotificationTypes.ReturnExpired, [], ct);
                break;
            case PaymentMessages.RefundCompleted:
                var refund = e.GetPayload<RefundCompletedV1>();
                await ViaOrderRef(e, refund.OrderId, NotificationTypes.RefundIssued, new() { ["refundAmount"] = Money.Format(refund.AmountCents) }, ct);
                break;
            default: throw new PermanentFailureException($"{e.MessageType} is not handled by the event consumer.");
        }
    }

    /// <summary>Keeps the newest contact (plan N12): an older event never overwrites a newer one.</summary>
    private Task UpsertContact(Envelope e, Guid accountId, string email, string name, CancellationToken ct)
    {
        var at = e.OccurredAt.ToUniversalTime();
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO contacts (account_id, email, name, updated_at) VALUES ({accountId}, {email}, {name}, {at})
            ON CONFLICT (account_id) DO UPDATE SET email = excluded.email, name = excluded.name, updated_at = excluded.updated_at
            WHERE contacts.updated_at <= excluded.updated_at
            """, ct);
    }

    private async Task AccountDeleted(Guid accountId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"DELETE FROM contacts WHERE account_id = {accountId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM notifications WHERE account_id = {accountId}", ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM order_refs WHERE account_id = {accountId}", ct);
        await db.Database.ExecuteSqlAsync($"UPDATE email_jobs SET status = {EmailStatuses.Cancelled} WHERE account_id = {accountId} AND status = {EmailStatuses.Pending}", ct);
        Disconnect(accountId);
    }

    private void Disconnect(Guid accountId) => db.AfterCommit.Add(() => Disconnects.PublishAsync(redis, accountId));

    /// <summary>Fulfillment and payment events carry only the order id: account and order number come from <c>order_refs</c> (§5.3).</summary>
    private async Task ViaOrderRef(Envelope e, Guid orderId, string type, Dictionary<string, string> vars, CancellationToken ct)
    {
        var order = await db.OrderRefs.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == orderId, ct)
            ?? throw new InvalidOperationException($"No order_refs row for order {orderId} yet."); // transient: the order event is usually milliseconds behind (§6.1 step 2)
        await Notify(e, type, order.AccountId, order.OrderNumber, vars, ct);
    }

    // order_refs values never change for an order (plan N12).
    private Task InsertOrderRef(Guid orderId, Guid accountId, string orderNumber, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO order_refs (order_id, account_id, order_number, updated_at) VALUES ({orderId}, {accountId}, {orderNumber}, now())
            ON CONFLICT (order_id) DO NOTHING
            """, ct);

    /// <summary>The notifying path (§6.1): in-app row and email job, each deduplicated by <c>(source_message_id, type | template)</c>.</summary>
    private async Task Notify(Envelope e, string type, Guid accountId, string orderNumber, Dictionary<string, string> vars, CancellationToken ct)
    {
        var contact = await db.Contacts.AsNoTracking().SingleOrDefaultAsync(c => c.AccountId == accountId, ct)
            ?? throw new InvalidOperationException($"No contacts row for account {accountId} yet."); // transient (§6.1 step 2; owner item 2 for a deleted account)
        vars["orderNumber"] = orderNumber;
        vars["name"] = contact.Name;

        var (title, body) = templates.RenderInApp(type, vars);
        var link = TemplateRenderer.InAppLink(orderNumber);
        var id = Guid.CreateVersion7();
        var created = await db.Database.SqlQuery<DateTimeOffset>($"""
            INSERT INTO notifications (id, account_id, type, title, body, link, source_message_id, created_at)
            VALUES ({id}, {accountId}, {type}, {title}, {body}, {link}, {e.MessageId}, now())
            ON CONFLICT (source_message_id, type) DO NOTHING
            RETURNING created_at AS "Value"
            """).ToListAsync(ct);
        if (created.Count == 1)
            db.AfterCommit.Add(async () =>
            {
                metrics.Created(type, "in_app"); // counted before the push: a failed push still created the row
                await hub.Clients.Group(NotificationHub.Group(accountId)).SendAsync(NotificationHub.ClientMethod, new PushMessage(id, type, title, link, created[0]), CancellationToken.None);
                metrics.Pushed(type);
            });

        await EnqueueEmail(e, type, accountId, contact.Email, vars, ct);
    }

    private async Task EnqueueEmail(Envelope e, string template, Guid? accountId, string to, Dictionary<string, string> data, CancellationToken ct)
    {
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO email_jobs (id, account_id, to_email, template, data, status, attempts, next_attempt_at, source_message_id, created_at)
            VALUES ({Guid.CreateVersion7()}, {accountId}, {to}, {template}, {JsonSerializer.Serialize(data)}::jsonb, {EmailStatuses.Pending}, 0, now(), {e.MessageId}, now())
            ON CONFLICT (source_message_id, template) DO NOTHING
            """, ct);
        if (inserted == 1) db.AfterCommit.Add(() => { metrics.Created(template, "email"); return Task.CompletedTask; });
    }
}
