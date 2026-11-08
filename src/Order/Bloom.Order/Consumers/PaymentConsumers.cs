using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Observability;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Consumers;

/// <summary>
/// <c>payment.succeeded.v1</c> (03-order §6.4, spec §4, §7), in <c>ConsumerHost</c>'s transaction (plan R8). A <c>pending_payment</c> order is paid by one guarded update, which is
/// also the paid-vs-expired race (§7): the expiry sweep takes the same row lock, so one of them moves the order. When the sweep was first, the order is read again and the late-payment
/// branch runs. A redelivery finds another status and does nothing.
/// </summary>
public sealed class PaymentSucceededConsumer(OrderDbContext db, Transitions transitions, Sagas sagas, Coupons coupons, OutboxWriter outbox, OrderMetrics metrics) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("payment", PaymentMessages.Succeeded)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var payment = envelope.GetPayload<PaymentSucceededV1>();
        if (payment.OrderId == Guid.Empty || payment.PaymentId == Guid.Empty) throw new PermanentFailureException("payment.succeeded.v1 needs an order id and a payment id.");

        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == payment.OrderId, ct);
        if (order is null) return;
        if (order.Status == OrderStatuses.PendingPayment)
        {
            if (await PayAsync(order, payment, ct)) return;
            order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == payment.OrderId, ct); // the sweep got there first
        }
        if (order.Status == OrderStatuses.Cancelled && order.CancelReason is CancelReasons.CheckoutExpired or CancelReasons.CheckoutFailed) await LateAsync(order, ct);
    }

    private async Task<bool> PayAsync(OrderRow order, PaymentSucceededV1 payment, CancellationToken ct)
    {
        var regular = order.Kind == OrderKinds.Regular;
        var moved = await transitions.MoveAsync(order.Id, [OrderStatuses.PendingPayment], regular ? OrderStatuses.Paid : OrderStatuses.AwaitingStock, Actor.System, null, null,
            s =>
            {
                s.SetProperty(o => o.DeadlineAt, (DateTimeOffset?)null).SetProperty(o => o.PaymentId, o => o.PaymentId ?? (Guid?)payment.PaymentId);
                if (regular) s.SetProperty(o => o.HandedToFulfillment, true);
            }, ct);
        if (!moved) return false;

        await coupons.MoveUseAsync(order.Id, CouponUseStatuses.Held, CouponUseStatuses.Consumed, ct);
        var lines = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == order.Id).OrderBy(l => l.LineNo).ToListAsync(ct);
        outbox.PublishEvent(OrderMessages.Paid, new OrderPaidV1(order.Id, order.Number, order.AccountId, order.LaunchId, payment.SucceededAt, order.TotalCents,
            [.. lines.Where(l => l.Kind == LineKinds.Product).Select(l => new OrderPaidLineV1(l.SkuId, l.Quantity))]));
        if (!regular) return true; // a pre-order waits for its stock (Task 11)

        outbox.PublishEvent(OrderMessages.ReadyToFulfill, Transitions.ReadyToFulfill(order, lines));
        await RemoveFromCartAsync(order.AccountId, lines, ct);
        return true;
    }

    /// <summary>
    /// Spec §7 "Cart removal on payment": each order line takes its quantity off the cart line with the same kind and SKU (a plan also on a cart device of the order parent's SKU); lines at 0 go,
    /// then the plans whose device went. Only what was bought leaves, whatever the customer changed meanwhile. The cart row is locked first, as every cart write does.
    /// </summary>
    private async Task RemoveFromCartAsync(Guid accountId, List<OrderLine> bought, CancellationToken ct)
    {
        var locked = await db.Database.SqlQuery<Guid>($"SELECT account_id AS \"Value\" FROM carts WHERE account_id = {accountId} FOR UPDATE").ToListAsync(ct);
        if (locked.Count == 0) return;
        var cart = await db.CartLines.Where(l => l.AccountId == accountId).ToListAsync(ct);
        var gone = new HashSet<CartLine>();
        var changed = false;
        foreach (var line in bought)
        {
            var parentSku = line.ParentLineNo is { } p ? bought.Single(b => b.LineNo == p).SkuId : (Guid?)null;
            var match = cart.FirstOrDefault(c => c.Kind == line.Kind && c.SkuId == line.SkuId
                && (parentSku is null || cart.Any(d => d.LineNo == c.ParentLineNo && d.SkuId == parentSku)));
            if (match is null) continue;
            changed = true;
            if (match.Quantity <= line.Quantity) gone.Add(match);
            else match.Quantity -= line.Quantity;
        }
        gone.UnionWith(cart.Where(c => c.Kind == LineKinds.WarrantyPlan && gone.Any(g => g.Kind == LineKinds.Product && g.LineNo == c.ParentLineNo)));
        if (!changed) return;

        db.CartLines.RemoveRange(gone);
        await db.Carts.Where(c => c.AccountId == accountId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Version, c => c.Version + 1), ct);
    }

    /// <summary>HLD-ADR-013: the customer paid an order that is gone; refund it in full. The saga is the once-only guard, and <c>cancel_reason</c> keeps its first value.</summary>
    private async Task LateAsync(OrderRow order, CancellationToken ct)
    {
        if (!await sagas.StartAsync(order.Id, SagaTypes.LatePayment, order.Kind, order.CancelReason!, ct)) return;
        outbox.PublishEvent(OrderMessages.Cancelled, new OrderCancelledV1(order.Id, order.Number, order.AccountId, SagaTypes.LatePayment));
        metrics.LatePayment();
    }
}

/// <summary><c>payment.failed.v1</c> (03-order §5.3): no effect. The order stays <c>pending_payment</c> and the customer retries on Stripe's page within the deadline (CHK-R13).</summary>
public sealed class PaymentFailedConsumer : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("payment", PaymentMessages.Failed)];

    public Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var failed = envelope.GetPayload<PaymentFailedV1>();
        if (failed.OrderId == Guid.Empty || failed.PaymentId == Guid.Empty) throw new PermanentFailureException("payment.failed.v1 needs an order id and a payment id.");
        return Task.CompletedTask;
    }
}
