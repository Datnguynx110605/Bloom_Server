using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Errors;
using Bloom.Shared.Persistence;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Services;

/// <summary>03-order §5.2 internal API (identity calls it; no HTTP route, no role check: Linkerd guards it, §9).</summary>
public sealed class InternalService(OrderDbContext db) : OrderInternal.OrderInternalBase
{
    private static readonly string[] UnsettledStatuses =
        [OrderStatuses.PendingPayment, OrderStatuses.Paid, OrderStatuses.AwaitingStock, OrderStatuses.Packing, OrderStatuses.Shipped];

    private static readonly TimeSpan ReturnWindow = TimeSpan.FromDays(14);

    /// <summary>ACC-R13: an order in flight, an open return, or a delivery less than 14 days ago (<c>creating</c> does not count).</summary>
    public override async Task<HasUnsettledOrdersResponse> HasUnsettledOrders(HasUnsettledOrdersRequest request, ServerCallContext context)
    {
        var account = Inputs.Id(request.AccountId, "account_id");
        var since = await db.Database.NowAsync(context.CancellationToken) - ReturnWindow;
        var unsettled = await db.Orders.AsNoTracking().AnyAsync(o => o.AccountId == account
            && (UnsettledStatuses.Contains(o.Status) || db.OrderReturns.Any(r => r.OrderId == o.Id && r.ClosedAt == null) || o.DeliveredAt > since), context.CancellationToken);
        return new HasUnsettledOrdersResponse { HasUnsettledOrders = unsettled };
    }
}
