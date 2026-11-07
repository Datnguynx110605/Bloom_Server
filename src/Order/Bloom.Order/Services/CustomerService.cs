using Bloom.Order.Api;
using Bloom.Order.Context;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Errors;
using Grpc.Core;

namespace Bloom.Order.Services;

/// <summary>03-order §5.1 <c>Order API</c>. The account comes from the gateway headers; another customer's order, and a <c>creating</c> one, is <c>NOT_FOUND</c> like an unknown id.</summary>
public sealed class CustomerService(OrderDbContext db, Cancellations cancellations) : OrderCustomer.OrderCustomerBase
{
    public override async Task<ListOrdersResponse> ListMyOrders(ListMyOrdersRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var (skip, take) = Inputs.Paging(request.Page, request.PageSize);
        return await Views.OrderPageAsync(Views.OwnOrders(db, caller.UserId), skip, take, context.CancellationToken);
    }

    public override async Task<Api.Order> GetOrder(GetOrderRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var id = Inputs.Id(request.OrderId, "order_id");
        return await Views.OrderAsync(db, Views.OwnOrders(db, caller.UserId), id, context.CancellationToken);
    }

    public override async Task<Api.Order> CancelOrder(CancelOrderRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var id = Inputs.Id(request.OrderId, "order_id");
        await cancellations.CancelAsync(id, request.Version, caller, null, context.CancellationToken);
        return await Views.OrderAsync(db, Views.OwnOrders(db, caller.UserId), id, context.CancellationToken);
    }
}
