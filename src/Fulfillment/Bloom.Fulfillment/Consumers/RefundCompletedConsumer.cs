using Bloom.Fulfillment.Data;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Consumers;

/// <summary>
/// <c>payment.refund-completed.v1</c> (spec §7 saga): the saga with this <c>refund_id</c> has no step left. An unknown id is an order's own refund: nothing to do.
/// Runs in <c>ConsumerHost</c>'s transaction (plan R8). A redelivery changes nothing: the update only touches an open saga.
/// </summary>
public sealed class RefundCompletedConsumer(FulfillmentDbContext db) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("payment", PaymentMessages.RefundCompleted)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var done = envelope.GetPayload<RefundCompletedV1>();
        string[] none = [];
        await db.Database.ExecuteSqlAsync($"UPDATE fulfillment_sagas SET pending_steps = {none}, completed_at = now() WHERE refund_id = {done.RefundId} AND completed_at IS NULL", ct);
    }
}
