using Bloom.Order.Data;
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
