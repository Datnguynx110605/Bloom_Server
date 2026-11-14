using Bloom.Catalog.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Context;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Errors;
using Bloom.Shared.Persistence;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using CartLine = Bloom.Order.Data.CartLine;

namespace Bloom.Order.Services;

/// <summary>03-order §5.1 <c>Cart API</c>. The cart is the caller's own: the account id comes from the gateway headers, never from the request.</summary>
public sealed class CartService(OrderDbContext db, Upstreams upstreams) : OrderCart.OrderCartBase
{
    public override async Task<Api.Cart> GetCart(GetCartRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        return await Views.CartAsync(db, upstreams, caller.UserId, context.CancellationToken);
    }

    /// <summary>
    /// Spec §4: a line is (<c>kind</c>, <c>sku_id</c>), plus <c>parent_line_no</c> for a plan; <c>quantity</c> is absolute and 0 removes it. The catalog checks run before the transaction
    /// (no lock is held over a call), the cart checks inside it, after the version guard.
    /// </summary>
    public override async Task<Api.Cart> UpsertCartLine(UpsertCartLineRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var ct = context.CancellationToken;
        if (!LineKinds.All.Contains(request.Kind)) throw OrderErrors.Validation("kind", "kind must be product or warranty_plan.");
        var skuId = Inputs.Id(request.SkuId, "sku_id");
        if (request.Quantity < 0) throw OrderErrors.Validation("quantity", "quantity must not be negative.");
        if (request.Version < 0) throw OrderErrors.Validation("version", "version must not be negative.");
        var isPlan = request.Kind == LineKinds.WarrantyPlan;
        if (isPlan != request.HasParentLineNo) throw OrderErrors.Validation("parent_line_no", "parent_line_no is for plan lines, and they need it.");
        if (!isPlan && request.Quantity > Inputs.MaxQuantity) throw OrderErrors.Error(OrderErrors.QuantityLimit, $"A product line holds at most {Inputs.MaxQuantity}.");
        int? parentNo = isPlan ? request.ParentLineNo : null;

        IReadOnlyDictionary<Guid, SkuPricing> pricing = new Dictionary<Guid, SkuPricing>();
        if (request.Quantity > 0) // a line can always be removed
        {
            var parentSku = parentNo is { } p
                ? await db.CartLines.AsNoTracking().Where(l => l.AccountId == caller.UserId && l.LineNo == p && l.Kind == LineKinds.Product).Select(l => (Guid?)l.SkuId).SingleOrDefaultAsync(ct)
                : null;
            pricing = await upstreams.PricingAsync(parentSku is { } ps ? [skuId, ps] : [skuId], ct);
            var sku = pricing[skuId];
            if (sku.Kind != "" && sku.Kind != request.Kind) throw OrderErrors.Validation("kind", $"The SKU is a {sku.Kind}.");
            if (sku.Launch is not null) throw OrderErrors.Error(OrderErrors.LaunchItemsInCart, "Launch items are bought through the launch, not the cart.");
            if (!sku.Buyable) throw OrderErrors.Error(OrderErrors.SkuNotBuyable, "The SKU cannot be bought.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await BumpVersionAsync(caller.UserId, request.Version, ct);
        var lines = await db.CartLines.Where(l => l.AccountId == caller.UserId).ToListAsync(ct);
        var line = lines.FirstOrDefault(l => l.Kind == request.Kind && l.SkuId == skuId && l.ParentLineNo == parentNo);
        if (request.Quantity == 0)
        {
            if (line is not null) RemoveWithPlans(lines, line);
        }
        else
        {
            if (isPlan) CheckPlan(lines, pricing, pricing[skuId], parentNo!.Value, request.Quantity);
            else if (line is not null && lines.Any(l => l.ParentLineNo == line.LineNo && l.Quantity > request.Quantity))
                throw OrderErrors.Error(OrderErrors.WarrantyNotEligible, "A device cannot go below the quantity of its plans.");

            if (line is not null) line.Quantity = request.Quantity;
            else db.CartLines.Add(new CartLine
            {
                AccountId = caller.UserId, LineNo = lines.Select(l => l.LineNo).DefaultIfEmpty().Max() + 1, Kind = request.Kind, SkuId = skuId, ParentLineNo = parentNo, Quantity = request.Quantity,
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Views.CartAsync(db, upstreams, caller.UserId, ct);
    }

    public override async Task<Api.Cart> RemoveCartLine(RemoveCartLineRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var ct = context.CancellationToken;
        if (request.Version < 0) throw OrderErrors.Validation("version", "version must not be negative.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await BumpVersionAsync(caller.UserId, request.Version, ct);
        var lines = await db.CartLines.Where(l => l.AccountId == caller.UserId).ToListAsync(ct);
        RemoveWithPlans(lines, lines.FirstOrDefault(l => l.LineNo == request.LineNo) ?? throw OrderErrors.NotFound("The cart line"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Views.CartAsync(db, upstreams, caller.UserId, ct);
    }

    /// <summary>
    /// The guarded write of 00-reliability §7.5: version 0 inserts the cart at 1, anything else adds 1 to a row at exactly that version. Zero rows is a stale version,
    /// and the row lock this takes serializes the cart's writers until the transaction ends.
    /// </summary>
    private async Task BumpVersionAsync(Guid accountId, long expected, CancellationToken ct)
    {
        int affected;
        if (expected == 0)
            affected = await db.Database.ExecuteSqlAsync($"INSERT INTO carts (account_id, version, updated_at) VALUES ({accountId}, 1, now()) ON CONFLICT (account_id) DO NOTHING", ct);
        else
        {
            var now = await db.Database.NowAsync(ct);
            affected = await db.Carts.Where(c => c.AccountId == accountId && c.Version == expected)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Version, c => c.Version + 1).SetProperty(c => c.UpdatedAt, now), ct);
        }
        if (affected != 1) throw OrderErrors.Conflict("cart");
    }

    private void RemoveWithPlans(List<CartLine> lines, CartLine line) =>
        db.CartLines.RemoveRange(lines.Where(l => l == line || (line.Kind == LineKinds.Product && l.ParentLineNo == line.LineNo)));

    /// <summary>The parent must be a product line of the cart whose product the plan covers, and the plan cannot outnumber it.</summary>
    private static void CheckPlan(List<CartLine> lines, IReadOnlyDictionary<Guid, SkuPricing> pricing, SkuPricing plan, int parentNo, int quantity)
    {
        var parent = lines.FirstOrDefault(l => l.LineNo == parentNo && l.Kind == LineKinds.Product) ?? throw OrderErrors.Validation("parent_line_no", "The cart has no such device line.");
        if (!pricing.TryGetValue(parent.SkuId, out var device)) // the device line changed after it was priced: the caller's cart is stale
            throw OrderErrors.Conflict("cart");
        if (!plan.EligibleProductIds.Contains(device.ProductId) || quantity > parent.Quantity)
            throw OrderErrors.Error(OrderErrors.WarrantyNotEligible, "The plan does not cover that device, or there are more plans than devices.");
    }
}
