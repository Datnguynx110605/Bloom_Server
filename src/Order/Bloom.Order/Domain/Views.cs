using Bloom.Order.Data;
using Bloom.Order.Errors;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Domain;

/// <summary>Entity to proto mapping for the Cart and staff APIs.</summary>
public static class Views
{
    /// <summary>
    /// The account's cart with each line's current catalog price and flags (spec §4). Prices are never stored. <c>UNAVAILABLE</c> when the catalog fails;
    /// when inventory fails <c>out_of_stock</c> is left out.
    /// </summary>
    public static async Task<Api.Cart> CartAsync(OrderDbContext db, Upstreams upstreams, Guid accountId, CancellationToken ct)
    {
        var view = new Api.Cart { Version = await db.Carts.AsNoTracking().Where(c => c.AccountId == accountId).Select(c => c.Version).SingleOrDefaultAsync(ct) };
        var lines = await db.CartLines.AsNoTracking().Where(l => l.AccountId == accountId).OrderBy(l => l.LineNo).ToListAsync(ct);
        if (lines.Count == 0) return view;

        var pricing = await upstreams.PricingAsync(lines.Select(l => l.SkuId), ct);
        var stock = await upstreams.AvailabilityAsync(lines.Where(l => l.Kind == LineKinds.Product).Select(l => l.SkuId), ct);
        view.Lines.AddRange(lines.Select(l =>
        {
            pricing.TryGetValue(l.SkuId, out var sku);
            var line = new Api.CartLine
            {
                LineNo = l.LineNo, Kind = l.Kind, SkuId = l.SkuId.ToString("D"), Quantity = l.Quantity, Name = sku?.Name ?? "", Available = sku?.Buyable ?? false,
            };
            if (l.ParentLineNo is { } parent) line.ParentLineNo = parent; // optional fields have no nullable property
            if (sku is { HasPriceCents: true }) line.PriceCents = sku.PriceCents;
            if (stock is not null && l.Kind == LineKinds.Product) line.OutOfStock = stock.GetValueOrDefault(l.SkuId) < l.Quantity;
            return line;
        }));
        return view;
    }

    /// <summary>What a customer may see of orders: their own, never <c>creating</c> (03-order §6.1). The one place that rule lives; anything outside it is <c>NOT_FOUND</c>.</summary>
    public static IQueryable<OrderRow> OwnOrders(OrderDbContext db, Guid accountId) =>
        db.Orders.AsNoTracking().Where(o => o.AccountId == accountId && o.Status != OrderStatuses.Creating);

    /// <summary>The order with its lines and history. <c>NOT_FOUND</c> when <paramref name="scope"/> does not hold it, the same error for every reason.</summary>
    public static async Task<Api.Order> OrderAsync(OrderDbContext db, IQueryable<OrderRow> scope, Guid id, CancellationToken ct)
    {
        var o = await scope.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw OrderErrors.NotFound("The order");
        var lines = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == id).OrderBy(l => l.LineNo).ToListAsync(ct);
        var history = await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == id).OrderBy(h => h.Id).ToListAsync(ct);
        var view = new Api.Order
        {
            Id = o.Id.ToString("D"), Number = o.Number, AccountId = o.AccountId.ToString("D"), Kind = o.Kind, LaunchId = o.LaunchId?.ToString("D") ?? "", Status = o.Status,
            CancelReason = o.CancelReason ?? "", ShipTo = new Api.ShipTo { Name = o.ShipTo.Name, Lines = { o.ShipTo.Lines }, City = o.ShipTo.City, State = o.ShipTo.State, Zip = o.ShipTo.Zip, Phone = o.ShipTo.Phone },
            ShippingOption = o.ShippingOption, Currency = o.Currency, SubtotalCents = o.SubtotalCents, DiscountCents = o.DiscountCents, TaxCents = o.TaxCents, ShippingCents = o.ShippingCents,
            TotalCents = o.TotalCents, TaxRateBp = o.TaxRateBp, Version = o.Version, CreatedAt = Timestamp.FromDateTimeOffset(o.CreatedAt),
            DeadlineAt = o.DeadlineAt is { } deadline ? Timestamp.FromDateTimeOffset(deadline) : null,
            DeliveredAt = o.DeliveredAt is { } delivered ? Timestamp.FromDateTimeOffset(delivered) : null,
        };
        view.Lines.AddRange(lines.Select(l =>
        {
            var line = new Api.OrderLine
            {
                LineNo = l.LineNo, Kind = l.Kind, SkuId = l.SkuId.ToString("D"), ProductId = l.ProductId.ToString("D"), CategoryId = l.CategoryId.ToString("D"), Name = l.Name,
                Serialized = l.Serialized, Quantity = l.Quantity, UnitPriceCents = l.UnitPriceCents, DiscountCents = l.DiscountCents, TaxCents = l.TaxCents, PaidCents = l.PaidCents,
                ReturnedQty = l.ReturnedQty, RefundedCents = l.RefundedCents,
            };
            if (l.ParentLineNo is { } parent) line.ParentLineNo = parent; // optional fields have no nullable property
            if (l.CoverageMonths is { } months) line.CoverageMonths = months;
            return line;
        }));
        view.History.AddRange(history.Select(h => new Api.StatusChange
        {
            FromStatus = h.FromStatus ?? "", ToStatus = h.ToStatus, ActorType = h.ActorType, ActorId = h.ActorId?.ToString("D") ?? "", Reason = h.Reason ?? "", At = Timestamp.FromDateTimeOffset(h.At),
        }));
        return view;
    }

    /// <summary>One page of <paramref name="orders"/>, newest first (<c>created_at DESC, id DESC</c>).</summary>
    public static async Task<Api.ListOrdersResponse> OrderPageAsync(IQueryable<OrderRow> orders, int skip, int take, CancellationToken ct)
    {
        var rows = await orders.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Skip(skip).Take(take).ToListAsync(ct);
        var response = new Api.ListOrdersResponse();
        response.Items.AddRange(rows.Select(o => new Api.OrderSummary
        {
            Id = o.Id.ToString("D"), Number = o.Number, Kind = o.Kind, Status = o.Status, TotalCents = o.TotalCents, CreatedAt = Timestamp.FromDateTimeOffset(o.CreatedAt),
        }));
        return response;
    }

    public static Api.Coupon CouponView(Data.Coupon c, IEnumerable<CouponScopeItem> items)
    {
        var view = new Api.Coupon
        {
            Id = c.Id.ToString("D"), Code = c.CodeDisplay, Type = c.Type, Value = c.Value, Scope = c.Scope, UsesCount = c.UsesCount, Active = c.Active, Version = c.Version,
            CreatedAt = Timestamp.FromDateTimeOffset(c.CreatedAt),
            StartsAt = c.StartsAt is { } starts ? Timestamp.FromDateTimeOffset(starts) : null,
            ExpiresAt = c.ExpiresAt is { } expires ? Timestamp.FromDateTimeOffset(expires) : null,
        };
        view.ScopeItems.AddRange(items.Select(i => new Api.ScopeItem { TargetType = i.TargetType, TargetId = i.TargetId.ToString("D") }));
        if (c.MinSpendCents is { } min) view.MinSpendCents = min; // optional fields have no nullable property
        if (c.TotalLimit is { } total) view.TotalLimit = total;
        if (c.PerCustomerLimit is { } perCustomer) view.PerCustomerLimit = perCustomer;
        return view;
    }

    public static Api.CheckoutSettings SettingsView(Data.CheckoutSettings s) => new()
    {
        ExpressFeeCents = s.ExpressFeeCents, Version = s.Version, UpdatedBy = s.UpdatedBy?.ToString("D") ?? "", UpdatedAt = Timestamp.FromDateTimeOffset(s.UpdatedAt),
    };
}
