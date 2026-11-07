using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Shared.Messaging;

namespace Bloom.Order.Domain;

/// <summary>A checkout that failed before payment (03-order §6.2, §6.9): the one composition checkout and the creating sweep share. Writes run in the caller's transaction.</summary>
public sealed class CheckoutFailures(Transitions transitions, Sagas sagas, OutboxWriter outbox)
{
    /// <summary>
    /// <c>creating → cancelled</c> (<c>checkout_failed</c>) with <c>order.cancelled.v1</c> (owner item 6) and, when something may be held, the compensation saga (plan R12).
    /// False when the order is no longer <c>creating</c> (a sweep or another request was first): nothing more to do.
    /// </summary>
    public async Task<bool> CancelAsync(OrderRow order, bool compensate, CancellationToken ct)
    {
        var moved = await transitions.MoveAsync(order.Id, [OrderStatuses.Creating], OrderStatuses.Cancelled, Actor.System, CancelReasons.CheckoutFailed, null,
            s => s.SetProperty(o => o.CancelReason, CancelReasons.CheckoutFailed), ct);
        if (!moved) return false;
        outbox.PublishEvent(OrderMessages.Cancelled, new OrderCancelledV1(order.Id, order.Number, order.AccountId, CancelReasons.CheckoutFailed));
        if (compensate) await sagas.StartAsync(order.Id, SagaTypes.CheckoutCompensation, order.Kind, afterPayment: false, CancelReasons.CheckoutFailed, ct);
        return true;
    }
}
