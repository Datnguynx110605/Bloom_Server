using System.Diagnostics;
using Bloom.Catalog.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Context;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Errors;
using Bloom.Order.Observability;
using Bloom.Payment.Contracts;
using Bloom.Shared.Errors;
using Bloom.Shared.Idempotency;
using Bloom.Shared.Persistence;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using CartLine = Bloom.Order.Data.CartLine;
using OrderLine = Bloom.Order.Data.OrderLine;

namespace Bloom.Order.Services;

/// <summary>
/// 03-order §6.2 <c>StartCheckout</c>, regular and launch orders. Tx 1 (the guard's claim and the hook below) commits the <c>creating</c> order before any hold exists (OR-02); the steps then
/// resume from the order's status, so a retry with the same key continues the same order. A rejection is thrown with its transaction still open: the guard saves it and stores
/// the answer in that transaction.
/// </summary>
public sealed class CheckoutService(
    OrderDbContext db, IdempotencyGuard guard, Upstreams upstreams, Transitions transitions, CheckoutFailures failures, Coupons coupons,
    OrderMetrics metrics, OrderOptions options, ILogger<CheckoutService> logger) : OrderCheckout.OrderCheckoutBase
{
    private const long MinTotalCents = 50; // CHK-R20: the card processor's minimum

    private static readonly TimeSpan CreatingWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(15);

    private TimeSpan _paymentCall; // left out of checkout_start_seconds (HLD-ADR-021)

    private sealed record Placement(IReadOnlyList<PricedLine> Lines, Totals Totals, Guid? CouponId);

    public override async Task<StartCheckoutResponse> StartCheckout(StartCheckoutRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer(); // the account id is the guard's claim: the gateway's x-bloom-user-id
        // R9, no I/O.
        var launch = request.SourceCase == StartCheckoutRequest.SourceOneofCase.Launch;
        if (launch) Inputs.LaunchLines(request.Launch);
        else if (request.SourceCase != StartCheckoutRequest.SourceOneofCase.Cart) throw OrderErrors.Validation("source", "source must be cart or launch.");
        var shipTo = Inputs.ShipTo(request.ShipTo ?? new ShipTo());
        if (!ShippingOptions.All.Contains(request.ShippingOption)) throw OrderErrors.Validation("shipping_option", "shipping_option must be standard or express.");
        if (request.HasCouponCode) Inputs.CouponCode(request.CouponCode, "coupon_code");

        var kind = launch ? OrderKinds.Launch : OrderKinds.Regular;
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await guard.ExecuteMultiStepAsync(request, context,
                (claim, ct) => ClaimAsync(claim, request, shipTo, ct),
                (claim, state, ct) => RunAsync(caller, request, shipTo, kind, state, ct));
        }
        finally
        {
            metrics.CheckoutStarted(kind, (Stopwatch.GetElapsedTime(started) - _paymentCall).TotalSeconds);
        }
    }

    /// <summary>Tx 1: the order of a resumed request (found through its key), or a new <c>creating</c> order with its key row and first history row.</summary>
    private async Task<(Guid OrderId, bool Existed)> ClaimAsync(IdempotencyClaim claim, StartCheckoutRequest request, ShipToV1 shipTo, CancellationToken ct)
    {
        var key = Guid.Parse(claim.Key);
        var existing = await db.CheckoutKeys.AsNoTracking().Where(k => k.AccountId == claim.UserId && k.IdempotencyKey == key).Select(k => (Guid?)k.OrderId).SingleOrDefaultAsync(ct);
        if (existing is { } orderId) return (orderId, true);

        var now = await db.Database.NowAsync(ct);
        var isLaunch = request.SourceCase == StartCheckoutRequest.SourceOneofCase.Launch;
        var order = new OrderRow
        {
            Id = Guid.CreateVersion7(), Number = "", AccountId = claim.UserId, Kind = isLaunch ? OrderKinds.Launch : OrderKinds.Regular, LaunchId = isLaunch ? Guid.Parse(request.Launch.LaunchId) : null, Status = OrderStatuses.Creating, DeadlineAt = now + CreatingWindow,
            ShippingOption = request.ShippingOption, ShipTo = shipTo, Currency = "USD", CreatedAt = now, UpdatedAt = now,
        };
        await OrderNumbers.InsertAsync(db, order, ct); // before the key row, which points at it
        db.CheckoutKeys.Add(new CheckoutKey { AccountId = claim.UserId, IdempotencyKey = key, OrderId = order.Id });
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, ToStatus = OrderStatuses.Creating, ActorType = ActorTypes.Customer, ActorId = claim.UserId, At = now });
        return (order.Id, false);
    }

    private async Task<StartCheckoutResponse> RunAsync(Caller caller, StartCheckoutRequest request, ShipToV1 shipTo, string kind, (Guid OrderId, bool Existed) state, CancellationToken ct)
    {
        db.ChangeTracker.Clear(); // a takeover runs this again on a context that still tracks what the lost run saved
        try
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == state.OrderId, ct);
            if (state.Existed && !SameRequest(order, request, shipTo)) // a retry may not change what the first request fixed (00-reliability §7.4); the creating sweep compensates the order
                throw BloomErrors.Create(StatusCode.InvalidArgument, BloomErrors.IdempotencyKeyReused, "This Idempotency-Key was already used for a different request.");
            if (order.Status == OrderStatuses.Creating) await PlaceAsync(order, request, caller, state.Existed, ct);

            var response = await PayAsync(state.OrderId, ct);
            metrics.Checkout(kind, "ok");
            return response;
        }
        catch (RpcException e) when (Upstreams.IsRejection(e)) // a stored outcome (once per outcome, not per transient failure)
        {
            metrics.Checkout(kind, Upstreams.BloomReason(e) ?? "");
            throw;
        }
    }

    /// <summary>What Tx 1 stored (source kind, launch, shipping option, address) against the request. Coupon, lines and pass token are not stored before Tx 2, so the request stays authoritative for them.</summary>
    private static bool SameRequest(OrderRow order, StartCheckoutRequest request, ShipToV1 shipTo) =>
        order.Kind == (request.SourceCase == StartCheckoutRequest.SourceOneofCase.Launch ? OrderKinds.Launch : OrderKinds.Regular)
        && (order.Kind == OrderKinds.Regular || order.LaunchId == Guid.Parse(request.Launch.LaunchId))
        && order.ShippingOption == request.ShippingOption
        && order.ShipTo with { Lines = [] } == shipTo with { Lines = [] } && order.ShipTo.Lines.SequenceEqual(shipTo.Lines);

    /// <summary>Steps 3 to 6 of §6.2: price and validate, hold the stock, then Tx 2.</summary>
    private async Task PlaceAsync(OrderRow order, StartCheckoutRequest request, Caller caller, bool existed, CancellationToken ct)
    {
        Placement placement;
        try
        {
            placement = await PriceAsync(order, request, ct);
            var products = placement.Lines.Where(l => l.Kind == LineKinds.Product).Select(l => (l.SkuId, l.Quantity));
            if (order.Kind == OrderKinds.Launch) await upstreams.HoldAsync(order.Id, request.Launch.PassToken, products, caller, ct);
            else await upstreams.ReserveAsync(order.Id, products, ct);
        }
        catch (RpcException e) when (Upstreams.IsRejection(e))
        {
            // A new order holds nothing to release (spec §7); a resumed one may hold from an earlier attempt that did not answer (owner item 2).
            await CancelAsync(order, compensate: existed, ct);
            throw;
        }

        await CommitPlacementAsync(order, placement, ct);
    }

    /// <summary>Cart lines (or the launch source's lines, numbered by position) to order lines, the validation of §6.2 step 3, the coupon pre-check and the totals.</summary>
    private async Task<Placement> PriceAsync(OrderRow order, StartCheckoutRequest request, CancellationToken ct)
    {
        var launch = order.Kind == OrderKinds.Launch;
        List<CartLine> cart;
        if (launch)
            cart = request.Launch.Lines.Select((l, i) => new CartLine
            {
                AccountId = order.AccountId, LineNo = i + 1, Kind = l.Kind, SkuId = Guid.Parse(l.SkuId), ParentLineNo = l.HasParentLineNo ? l.ParentLineNo : null, Quantity = l.Quantity,
            }).ToList();
        else
        {
            cart = await db.CartLines.AsNoTracking().Where(l => l.AccountId == order.AccountId).OrderBy(l => l.LineNo).ToListAsync(ct);
            if (cart.Count == 0) throw OrderErrors.Error(OrderErrors.CartEmpty, "The cart is empty.");
        }
        var pricing = await upstreams.PricingAsync(cart.Select(l => l.SkuId), ct);

        // Products keep their cart order; the plans follow, grouped under their device, and point at its new number.
        var products = cart.Where(l => l.Kind == LineKinds.Product).ToList();
        var numbers = products.Select((l, i) => (l.LineNo, No: i + 1)).ToDictionary(x => x.LineNo, x => x.No);
        var lines = products.Select((l, i) => Line(l, i + 1, null)).ToList();
        foreach (var plan in cart.Where(l => l.Kind == LineKinds.WarrantyPlan).OrderBy(l => l.ParentLineNo is { } p && numbers.TryGetValue(p, out var n) ? n : int.MaxValue).ThenBy(l => l.LineNo))
        {
            var parent = plan.ParentLineNo is { } p && numbers.TryGetValue(p, out var no) ? lines[no - 1] : null;
            var line = Line(plan, lines.Count + 1, parent?.LineNo);
            if (parent is null || plan.Quantity > parent.Quantity || !pricing[plan.SkuId].EligibleProductIds.Contains(parent.ProductId.ToString("D")))
                throw OrderErrors.Error(OrderErrors.WarrantyNotEligible, "A warranty plan must cover a device in the order, and there can be no more plans than devices.");
            lines.Add(line);
        }

        Guid? couponId = null;
        CouponTerms? terms = null;
        if (request.HasCouponCode)
        {
            if (launch) throw OrderErrors.Error(OrderErrors.CouponNotApplicable, "A coupon does not apply to a launch order (LCH-R14)."); // after the lines, which are checked first
            var (id, checkedTerms) = await coupons.CheckAsync(Inputs.CouponCode(request.CouponCode, "coupon_code").Normalized, order.AccountId, ct);
            (couponId, terms) = (id, checkedTerms);
        }
        var shipping = order.ShippingOption == ShippingOptions.Express ? await db.CheckoutSettings.AsNoTracking().Select(s => s.ExpressFeeCents).SingleAsync(ct) : 0;
        var totals = Pricing.Compute(lines, terms, launchOrder: launch, options.TaxRateBasisPoints!.Value, shipping);
        if (totals.TotalCents < MinTotalCents) throw OrderErrors.Error(OrderErrors.OrderTotalTooLow, "The order total must be at least $0.50."); // CHK-R20
        return new Placement(lines, totals, couponId);

        PricedLine Line(CartLine l, int no, int? parentNo)
        {
            // A launch product is judged by its launch flow below (catalog says buyable = false while pre-orders are open, 08-catalog §6.1); a plan still has to be buyable.
            var byLaunch = launch && l.Kind == LineKinds.Product;
            if (!pricing.TryGetValue(l.SkuId, out var sku) || !(sku.Buyable || byLaunch) || !sku.HasPriceCents || sku.Kind != l.Kind)
                throw OrderErrors.Error(OrderErrors.SkuNotBuyable, "An item cannot be bought any more.");
            if (byLaunch)
            {
                if (sku.Launch is not { } info || Guid.Parse(info.LaunchId) != order.LaunchId)
                    throw OrderErrors.Error(OrderErrors.MixedLaunchOrder, "A launch order holds only SKUs of its launch.");
                if (info.Stage != "preorder_open") throw OrderErrors.Error(OrderErrors.SkuNotBuyable, "The launch is not open for pre-orders.");
            }
            else if (!launch && sku.Launch is { Stage: not "released" })
                throw OrderErrors.Error(OrderErrors.SkuNotBuyable, "An item cannot be bought any more.");
            if (!launch && l.Kind == LineKinds.Product && l.Quantity > Inputs.MaxQuantity)
                throw OrderErrors.Error(OrderErrors.QuantityLimit, $"A product line holds at most {Inputs.MaxQuantity}.");
            return new PricedLine(no, l.Kind, l.SkuId, Guid.Parse(sku.ProductId), Guid.Parse(sku.CategoryId), sku.Name, sku.Serialized, l.Quantity, parentNo,
                l.Kind == LineKinds.WarrantyPlan ? sku.CoverageMonths : null, sku.PriceCents);
        }
    }

    /// <summary>
    /// Tx 2: with the order locked and still <c>creating</c>, hold the coupon (§6.8), insert the lines, set the totals and the tax rate, and move to <c>pending_payment</c>.
    /// A coupon that cannot be held after all cancels the order with its compensation saga, in this transaction, and throws.
    /// </summary>
    private async Task CommitPlacementAsync(OrderRow order, Placement placement, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct); // not disposed on the rejection path: the guard commits it
        var locked = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM orders WHERE id = {order.Id} AND status = {OrderStatuses.Creating} FOR UPDATE").ToListAsync(ct);
        if (locked.Count == 0)
        {
            await tx.DisposeAsync();
            throw OrderErrors.Error(OrderErrors.CheckoutClosed, "This checkout was cancelled; start a new one.");
        }

        if (placement.CouponId is { } couponId)
        {
            try
            {
                await coupons.HoldAsync(couponId, order.AccountId, order.Id, ct);
            }
            catch (RpcException e) when (Upstreams.IsRejection(e))
            {
                await failures.CancelAsync(order, compensate: true, ct); // the stock is held
                throw;
            }
        }

        var amounts = placement.Totals.Lines.ToDictionary(a => a.LineNo);
        db.OrderLines.AddRange(placement.Lines.Select(l => new OrderLine
        {
            OrderId = order.Id, LineNo = l.LineNo, Kind = l.Kind, SkuId = l.SkuId, ProductId = l.ProductId, CategoryId = l.CategoryId, Name = l.Name, Serialized = l.Serialized,
            ParentLineNo = l.ParentLineNo, CoverageMonths = l.CoverageMonths, Quantity = l.Quantity, UnitPriceCents = l.UnitPriceCents,
            DiscountCents = amounts[l.LineNo].DiscountCents, TaxCents = amounts[l.LineNo].TaxCents, PaidCents = amounts[l.LineNo].PaidCents,
        }));
        await db.SaveChangesAsync(ct);
        var t = placement.Totals;
        var deadline = await db.Database.NowAsync(ct) + PaymentWindow;
        var moved = await transitions.MoveAsync(order.Id, [OrderStatuses.Creating], OrderStatuses.PendingPayment, new Actor(ActorTypes.Customer, order.AccountId), null, null,
            s => s.SetProperty(o => o.SubtotalCents, t.SubtotalCents).SetProperty(o => o.DiscountCents, t.DiscountCents).SetProperty(o => o.TaxCents, t.TaxCents)
                .SetProperty(o => o.ShippingCents, t.ShippingCents).SetProperty(o => o.TotalCents, t.TotalCents).SetProperty(o => o.TaxRateBp, options.TaxRateBasisPoints!.Value)
                .SetProperty(o => o.DeadlineAt, deadline), ct);
        if (!moved) throw new InvalidOperationException("The order left 'creating' while it was locked."); // cannot happen: the row lock is ours
        await tx.CommitAsync(ct);
        await tx.DisposeAsync();
    }

    /// <summary>
    /// Steps 7 and 8: <c>CreatePayment</c>, then <c>payment_id</c> and the response, in a transaction the guard commits with the stored answer. The order's status decides (§6.2 resume, D21):
    /// <c>cancelled</c> (swept while the key was released, owner item 1) and <c>pending_payment</c> past its deadline (R9, the database clock) are <c>CHECKOUT_CLOSED</c>;
    /// <c>paid</c>, <c>awaiting_stock</c> or later answer normally with no <c>payment_url</c>, which payment has no session left to give.
    /// </summary>
    private async Task<StartCheckoutResponse> PayAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, ct);
        if (order.Status == OrderStatuses.Cancelled
            || (order.Status == OrderStatuses.PendingPayment && await db.Database.SqlQuery<bool>($"SELECT COALESCE(deadline_at <= now(), false) AS \"Value\" FROM orders WHERE id = {orderId}").SingleAsync(ct)))
            throw OrderErrors.Error(OrderErrors.CheckoutClosed, "This checkout was cancelled; start a new one.");
        if (order.Status != OrderStatuses.PendingPayment) return Response(order, "");

        var lines = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == orderId).OrderBy(l => l.LineNo).ToListAsync(ct);
        var display = lines.Select(l => new DisplayLine { Name = $"{l.Name} × {l.Quantity}", AmountCents = l.PaidCents }).ToList();
        if (order.ShippingCents > 0) display.Add(new DisplayLine { Name = "Express shipping", AmountCents = order.ShippingCents }); // they sum to the total (spec §7)

        CreatePaymentResponse payment;
        var paymentStarted = Stopwatch.GetTimestamp();
        try
        {
            payment = await upstreams.CreatePaymentAsync(order.Id, order.Number, order.AccountId, order.TotalCents, display, ct);
        }
        catch (RpcException e) // every failure, a business status too: the order stays pending_payment for a retry or the expiry sweep (owner item 3)
        {
            logger.LogError(e, "CreatePayment failed for order {OrderId}; the order stays pending_payment.", order.Id);
            throw new RpcException(new Status(StatusCode.Unavailable, "The payment could not be started; try again."));
        }
        finally
        {
            _paymentCall += Stopwatch.GetElapsedTime(paymentStarted); // += : a takeover may run this step again
        }

        _ = await db.Database.BeginTransactionAsync(ct); // the guard saves and commits it with the stored response
        var paymentId = Guid.Parse(payment.PaymentId);
        await db.Orders.Where(o => o.Id == orderId && o.PaymentId == null).ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentId, paymentId), ct);
        return Response(order, payment.CheckoutUrl);
    }

    private static StartCheckoutResponse Response(OrderRow order, string paymentUrl) => new()
    {
        OrderId = order.Id.ToString("D"), Number = order.Number, SubtotalCents = order.SubtotalCents, DiscountCents = order.DiscountCents, TaxCents = order.TaxCents,
        ShippingCents = order.ShippingCents, TotalCents = order.TotalCents, PaymentUrl = paymentUrl, DeadlineAt = order.DeadlineAt is { } deadline ? Timestamp.FromDateTimeOffset(deadline) : null,
    };

    /// <summary>Opens a transaction and cancels the order in it, leaving it open for the guard (the caller rethrows the rejection).</summary>
    private async Task CancelAsync(OrderRow order, bool compensate, CancellationToken ct)
    {
        _ = await db.Database.BeginTransactionAsync(ct);
        await failures.CancelAsync(order, compensate, ct);
    }
}
