using Bloom.Order.Context;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Errors;
using Bloom.Order.Observability;
using Bloom.Shared.Errors;
using Bloom.Shared.Messaging;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Domain;

/// <summary>03-order §6.6: the cancellation of a paid order by its customer or by Support (ORD-R05, ORD-R14).</summary>
public sealed class Cancellations(
    OrderDbContext db, Upstreams upstreams, Transitions transitions, Coupons coupons, Sagas sagas, OutboxWriter outbox, Audit audit, OrderMetrics metrics)
{
    private static readonly string[] CustomerStatuses = [OrderStatuses.Paid, OrderStatuses.AwaitingStock];
    private static readonly string[] SupportStatuses = [OrderStatuses.Paid, OrderStatuses.AwaitingStock, OrderStatuses.Packing];

    /// <summary>
    /// The order, version and status are checked first; then fulfillment is asked (outside any transaction) when the order was handed to it; then one transaction does the rest.
    /// Every refusal leaves the order untouched. <paramref name="supportReason"/> is Support's reason (kept in the history) and marks the caller as Support.
    /// </summary>
    public async Task CancelAsync(Guid orderId, long version, Caller caller, string? supportReason, CancellationToken ct)
    {
        var support = supportReason is not null;
        var by = support ? CancelReasons.Support : CancelReasons.Customer;
        var order = await (support ? db.Orders.AsNoTracking() : Views.OwnOrders(db, caller.UserId)).SingleOrDefaultAsync(o => o.Id == orderId, ct) ?? throw OrderErrors.NotFound("The order");
        if (order.Version != version) throw Stale();
        var allowed = support ? SupportStatuses : CustomerStatuses;
        if (!allowed.Contains(order.Status)) throw NotAllowed(by);

        if (order.HandedToFulfillment)
        {
            try { await upstreams.CancelPackTaskAsync(orderId, by, ct); }
            catch (RpcException e) when (Upstreams.BloomReason(e) is OrderErrors.CancelNotAllowed or OrderErrors.AlreadyShipped)
            {
                metrics.Cancellation(by, Upstreams.BloomReason(e) == OrderErrors.AlreadyShipped ? "already_shipped" : "not_allowed");
                throw;
            }
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = new Actor(support ? ActorTypes.Staff : ActorTypes.Customer, caller.UserId);
        // A concurrent change after fulfillment agreed is answered as it is found; fulfillment's CancelPackTask is idempotent, so a retry is safe.
        if (!await transitions.MoveAsync(orderId, allowed, OrderStatuses.Cancelled, actor, supportReason, version, s => s.SetProperty(o => o.CancelReason, by), ct)) throw NotAllowed(by);
        await coupons.MoveUseAsync(orderId, CouponUseStatuses.Consumed, CouponUseStatuses.Restored, ct);
        await sagas.StartAsync(orderId, SagaTypes.Cancellation, order.Kind, afterPayment: true, by, ct);
        outbox.PublishEvent(OrderMessages.Cancelled, new OrderCancelledV1(orderId, order.Number, order.AccountId, by));
        if (support) audit.Record(caller.UserId, caller.Role!, "order.support-cancel-order", "order", orderId.ToString("D"), new { reason = supportReason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.Cancellation(by, "ok");
    }

    private RpcException NotAllowed(string by)
    {
        metrics.Cancellation(by, "not_allowed");
        return OrderErrors.Error(OrderErrors.CancelNotAllowed, "The order can no longer be cancelled.");
    }

    private static RpcException Stale() => BloomErrors.Create(StatusCode.Aborted, BloomErrors.ConcurrencyConflict, "The order was changed by someone else; reload and retry.");
}
