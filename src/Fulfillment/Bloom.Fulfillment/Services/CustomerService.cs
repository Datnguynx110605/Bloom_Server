using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Context;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Errors;
using Bloom.Fulfillment.V1;
using Bloom.Shared.Messaging;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using ReturnRequest = Bloom.Fulfillment.Data.ReturnRequest;
using ReturnUnit = Bloom.Fulfillment.Data.ReturnUnit;
using Shipment = Bloom.Fulfillment.Data.Shipment;

namespace Bloom.Fulfillment.Services;

/// <summary>05-fulfillment §3 component <c>Customer API</c> (returns). The caller's id is the order's <c>account_id</c>; anything that is not theirs is <c>NOT_FOUND</c> (spec §6).</summary>
public sealed class CustomerService(FulfillmentDbContext db, OutboxWriter outbox, TimeProvider clock) : FulfillmentCustomer.FulfillmentCustomerBase
{
    private static readonly TimeSpan ReturnWindow = TimeSpan.FromDays(14); // RET-R01, RET-R15
    private static readonly string[] Open =
        [ReturnStatuses.Requested, ReturnStatuses.LabelIssued, ReturnStatuses.InTransit, ReturnStatuses.Received, ReturnStatuses.Inspected];

    /// <summary>§6.4 steps 1-5 under the order's <c>order_accounts</c> lock (it serializes the unit counts). A product request gets its units, its <c>return</c> shipment (<c>label_pending</c>) and <c>return-requested</c>.</summary>
    public override async Task<V1.ReturnRequest> RequestReturn(RequestReturnRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var orderId = Inputs.Id(request.OrderId, "order_id");
        var ct = context.CancellationToken;
        if (request.Units.Count == 0 || request.Units.Any(u => u.Quantity <= 0) || request.Units.Select(u => u.LineNo).Distinct().Count() != request.Units.Count)
            throw FulfillmentErrors.Validation("units", "Give each line once with a quantity above 0.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var account = (await db.OrderAccounts.FromSql($"SELECT * FROM order_accounts WHERE order_id = {orderId} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (account is null || account.AccountId != caller.UserId) throw FulfillmentErrors.NotFound("The order");
        var now = clock.GetUtcNow();
        if (account.DeliveredAt is not { } delivered || now > delivered + ReturnWindow)
            throw FulfillmentErrors.Error(FulfillmentErrors.ReturnWindowClosed, "The return window of this order is closed.");

        var lines = await db.OrderLineAccounts.AsNoTracking().Where(l => l.OrderId == orderId).ToDictionaryAsync(l => l.LineNo, ct);
        if (request.Units.Any(u => !lines.ContainsKey(u.LineNo))) throw FulfillmentErrors.Validation("units", "The order has no such line.");
        var plans = request.Units.Count(u => lines[u.LineNo].Kind == LineKinds.WarrantyPlan);
        if (plans > 0 && plans < request.Units.Count) throw FulfillmentErrors.Validation("units", "A request is either plan-only or product-only."); // owner item 2
        if (plans > 0) throw FulfillmentErrors.Validation("units", "Plan-only requests are not available yet."); // Task 9

        var openUnits = await db.ReturnUnits.AsNoTracking()
            .Where(u => db.ReturnRequests.Any(r => r.Id == u.ReturnId && r.OrderId == orderId && Open.Contains(r.Status)))
            .GroupBy(u => u.LineNo).Select(g => new { LineNo = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.LineNo, g => g.Count, ct);
        foreach (var u in request.Units)
        {
            var line = lines[u.LineNo];
            if (u.Quantity > line.Quantity - line.RefundedUnits - openUnits.GetValueOrDefault(u.LineNo))
                throw FulfillmentErrors.Error(FulfillmentErrors.ReturnQuantityExceeded, $"Line {u.LineNo} has fewer than {u.Quantity} units left to return.");
        }

        var returnId = Guid.CreateVersion7();
        var shipmentId = Guid.CreateVersion7();
        db.ReturnRequests.Add(new ReturnRequest
        {
            Id = returnId, OrderId = orderId, AccountId = account.AccountId, Status = ReturnStatuses.Requested, ExpiresAt = now + ReturnWindow,
            ReturnShipmentId = shipmentId, CreatedAt = now, UpdatedAt = now,
        });
        db.Shipments.Add(new Shipment
        {
            Id = shipmentId, ReturnRequestId = returnId, OrderId = orderId, Direction = Directions.Return, Service = ShippingOptions.Standard,
            Status = ShipmentStatuses.LabelPending, NextAttemptAt = now, CreatedAt = now,
        });
        var unitNo = 0;
        foreach (var u in request.Units)
            for (var i = 0; i < u.Quantity; i++)
                db.ReturnUnits.Add(new ReturnUnit { ReturnId = returnId, UnitNo = ++unitNo, LineNo = u.LineNo });
        outbox.PublishEvent(FulfillmentMessages.ReturnRequested, new ReturnRequestedV1(orderId, returnId));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Views.ReturnAsync(db, returnId, ct);
    }

    /// <summary>The caller's returns, newest first.</summary>
    public override async Task<ListMyReturnsResponse> ListMyReturns(ListMyReturnsRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var (skip, take) = Inputs.Paging(request.Page, request.PageSize);
        var page = await db.ReturnRequests.AsNoTracking().Where(r => r.AccountId == caller.UserId)
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip(skip).Take(take).ToListAsync(context.CancellationToken);
        return new ListMyReturnsResponse { Items = { await Views.ReturnsAsync(db, page, context.CancellationToken) } };
    }
}
