using Bloom.Fulfillment.Contracts;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Consumers;

// Fulfillment's events (03-order §5.3, §6.1, §6.7), each in ConsumerHost's transaction. Every status change is Transitions.MoveAsync with the system actor; an unknown order or an order in
// another status is a no-op, which is how reordered and reship events (ORD-R13) do nothing. Events may skip ahead (§6.1, D19): a late earlier event finds the order further on.

/// <summary><c>fulfillment.packing-started.v1</c>: <c>paid → packing</c>.</summary>
public sealed class PackingStartedConsumer(Transitions transitions) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("fulfillment", FulfillmentMessages.PackingStarted)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct) =>
        await transitions.MoveAsync(envelope.GetPayload<PackingStartedV1>().OrderId, [OrderStatuses.Paid], OrderStatuses.Packing, Actor.System, null, null, null, ct);
}

/// <summary><c>fulfillment.shipment-shipped.v1</c>: <c>paid | packing → shipped</c> (D19). A reship's shipped finds the order past <c>packing</c> and does nothing.</summary>
public sealed class ShipmentShippedConsumer(Transitions transitions) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("fulfillment", FulfillmentMessages.ShipmentShipped)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct) =>
        await transitions.MoveAsync(envelope.GetPayload<ShipmentShippedV1>().OrderId, [OrderStatuses.Paid, OrderStatuses.Packing], OrderStatuses.Shipped, Actor.System, null, null, null, ct);
}

/// <summary>
/// <c>fulfillment.shipment-delivered.v1</c>: <c>paid | packing | shipped → delivered</c> (D19) with <c>delivered_at</c> from the message, then <c>order.delivered.v1</c> with the distinct product ids of the product lines.
/// <c>RefId</c> is ignored: a reship's delivery moves a <c>shipped</c> order the same way (spec §7).
/// </summary>
public sealed class ShipmentDeliveredConsumer(OrderDbContext db, Transitions transitions, OutboxWriter outbox) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("fulfillment", FulfillmentMessages.ShipmentDelivered)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var delivered = envelope.GetPayload<ShipmentDeliveredV1>();
        var at = delivered.DeliveredAt;
        if (!await transitions.MoveAsync(delivered.OrderId, [OrderStatuses.Paid, OrderStatuses.Packing, OrderStatuses.Shipped], OrderStatuses.Delivered, Actor.System, null, null, s => s.SetProperty(o => o.DeliveredAt, (DateTimeOffset?)at), ct)) return;

        var accountId = await db.Orders.AsNoTracking().Where(o => o.Id == delivered.OrderId).Select(o => o.AccountId).SingleAsync(ct);
        var products = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == delivered.OrderId && l.Kind == LineKinds.Product).Select(l => l.ProductId).Distinct().ToListAsync(ct);
        outbox.PublishEvent(OrderMessages.Delivered, new OrderDeliveredV1(delivered.OrderId, accountId, products, at));
    }
}

/// <summary><c>fulfillment.delivery-failed.v1</c> (ORD-R17): <c>paid | packing | shipped → delivery_failed</c> (D19), and the order's coupon use <c>consumed → restored</c>.</summary>
public sealed class DeliveryFailedConsumer(Transitions transitions, Coupons coupons) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("fulfillment", FulfillmentMessages.DeliveryFailed)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var failed = envelope.GetPayload<DeliveryFailedV1>();
        if (await transitions.MoveAsync(failed.OrderId, [OrderStatuses.Paid, OrderStatuses.Packing, OrderStatuses.Shipped], OrderStatuses.DeliveryFailed, Actor.System, null, null, null, ct))
            await coupons.MoveUseAsync(failed.OrderId, CouponUseStatuses.Consumed, CouponUseStatuses.Restored, ct);
    }
}

/// <summary><c>fulfillment.return-requested.v1</c>: <c>open_returns + 1</c>.</summary>
public sealed class ReturnRequestedConsumer(OrderDbContext db, TimeProvider clock) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("fulfillment", FulfillmentMessages.ReturnRequested)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Orders.Where(o => o.Id == envelope.GetPayload<ReturnRequestedV1>().OrderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.OpenReturns, o => o.OpenReturns + 1).SetProperty(o => o.Version, o => o.Version + 1).SetProperty(o => o.UpdatedAt, now), ct);
    }
}

/// <summary><c>fulfillment.return-closed.v1</c>: <c>open_returns − 1</c>, never below 0.</summary>
public sealed class ReturnClosedConsumer(OrderDbContext db, TimeProvider clock) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("fulfillment", FulfillmentMessages.ReturnClosed)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Orders.Where(o => o.Id == envelope.GetPayload<ReturnClosedV1>().OrderId && o.OpenReturns > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.OpenReturns, o => o.OpenReturns - 1).SetProperty(o => o.Version, o => o.Version + 1).SetProperty(o => o.UpdatedAt, now), ct);
    }
}

/// <summary>
/// <c>fulfillment.return-decided.v1</c> (§6.7, owner item 5): adds each accepted quantity to its line's <c>returned_qty</c> (plans included; <c>ck_order_lines_returned_qty_in_range</c> stops an overflow).
/// When anything was accepted, <c>delivered | partially_returned</c> becomes <c>returned</c> if every product line is fully returned, else <c>partially_returned</c>. Nothing accepted: unchanged.
/// </summary>
public sealed class ReturnDecidedConsumer(OrderDbContext db, Transitions transitions) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("fulfillment", FulfillmentMessages.ReturnDecided)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var decided = envelope.GetPayload<ReturnDecidedV1>();
        if (decided.Accepted.Count == 0) return;

        string[] from = [OrderStatuses.Delivered, OrderStatuses.PartiallyReturned];
        var status = (await db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM orders WHERE id = {decided.OrderId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (status is null || !from.Contains(status)) return;

        foreach (var line in decided.Accepted)
        {
            if (line.Quantity <= 0) throw new PermanentFailureException("fulfillment.return-decided.v1 has an accepted quantity that is not positive.");
            var quantity = line.Quantity;
            var updated = await db.OrderLines.Where(l => l.OrderId == decided.OrderId && l.LineNo == line.LineNo)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ReturnedQty, l => l.ReturnedQty + quantity), ct);
            if (updated == 0) throw new PermanentFailureException($"fulfillment.return-decided.v1 names line {line.LineNo}, which the order does not have.");
        }

        var allReturned = !await db.OrderLines.AnyAsync(l => l.OrderId == decided.OrderId && l.Kind == LineKinds.Product && l.ReturnedQty < l.Quantity, ct);
        await transitions.MoveAsync(decided.OrderId, from, allReturned ? OrderStatuses.Returned : OrderStatuses.PartiallyReturned, Actor.System, null, null, null, ct);
    }
}
