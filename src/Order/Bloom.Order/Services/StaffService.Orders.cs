using Bloom.Order.Api;
using Bloom.Order.Context;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Errors;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Services;

/// <summary>Orders for Support and Admin (plan R7): any order, and the cancellation Support may do during packing.</summary>
public sealed partial class StaffService
{
    public override async Task<Api.Order> GetOrder(GetOrderRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.OrderStaff);
        var id = Inputs.Id(request.OrderId, "order_id");
        return await Views.OrderAsync(db, db.Orders.AsNoTracking(), id, context.CancellationToken);
    }

    /// <summary>Newest first; <c>number</c> (exact), <c>account_id</c> and <c>status</c> combine.</summary>
    public override async Task<ListOrdersResponse> SearchOrders(SearchOrdersRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.OrderStaff);
        var (skip, take) = Inputs.Paging(request.Page, request.PageSize);
        var account = request.HasAccountId ? Inputs.Id(request.AccountId, "account_id") : (Guid?)null;
        if (request.HasStatus && !OrderStatuses.All.Contains(request.Status)) throw OrderErrors.Validation("status", "status must be an order status.");

        var query = db.Orders.AsNoTracking();
        if (request.HasNumber) query = query.Where(o => o.Number == request.Number);
        if (account is { } a) query = query.Where(o => o.AccountId == a);
        if (request.HasStatus) query = query.Where(o => o.Status == request.Status);
        return await Views.OrderPageAsync(query, skip, take, context.CancellationToken);
    }

    public override async Task<Api.Order> SupportCancelOrder(SupportCancelOrderRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.OrderStaff);
        var id = Inputs.Id(request.OrderId, "order_id");
        var reason = request.Reason.Trim();
        if (reason is not { Length: >= 1 and <= 500 }) throw OrderErrors.Validation("reason", "reason must be 1 to 500 characters.");
        await cancellations.CancelAsync(id, request.Version, caller, reason, context.CancellationToken);
        return await Views.OrderAsync(db, db.Orders.AsNoTracking(), id, context.CancellationToken);
    }
}
