using System.Diagnostics;
using Bloom.Catalog.Contracts;
using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Observability;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Services;

/// <summary>LLD 08-catalog §3 component <c>Internal API</c> (§6.8): order, launch and inventory call it. Reads Postgres only (CR-02), never Redis.</summary>
public sealed class InternalService(CatalogDbContext db, CatalogMetrics metrics) : CatalogInternal.CatalogInternalBase
{
    public override async Task<GetPricingResponse> GetPricing(GetPricingRequest request, ServerCallContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var ct = context.CancellationToken;
        var ids = request.SkuIds.Distinct().ToList();
        var parsed = ids.Select(i => Guid.TryParse(i, out var g) ? g : (Guid?)null).OfType<Guid>().ToList();

        var rows = (await db.Skus.AsNoTracking().Where(s => parsed.Contains(s.Id))
            .Join(db.Products, s => s.ProductId, p => p.Id, (s, p) => new { Sku = s, Product = p }).ToListAsync(ct))
            .ToDictionary(r => r.Sku.Id);
        var optionValues = (await (
            from sov in db.SkuOptionValues
            where parsed.Contains(sov.SkuId)
            join ov in db.OptionValues on sov.OptionValueId equals ov.Id
            join ot in db.OptionTypes on ov.OptionTypeId equals ot.Id
            orderby ot.Position
            select new { sov.SkuId, ov.Value }).AsNoTracking().ToListAsync(ct))
            .ToLookup(x => x.SkuId, x => x.Value);
        var planIds = rows.Values.Where(r => r.Product.Kind == ProductKinds.WarrantyPlan).Select(r => r.Product.Id).Distinct().ToList();
        var eligible = (await db.PlanEligibilities.AsNoTracking().Where(e => planIds.Contains(e.PlanProductId)).ToListAsync(ct))
            .ToLookup(e => e.PlanProductId, e => e.EligibleProductId.ToString());

        var response = new GetPricingResponse();
        foreach (var id in ids)
        {
            var item = new SkuPricing { SkuId = id };
            if (Guid.TryParse(id, out var guid) && rows.TryGetValue(guid, out var row))
            {
                var (sku, product) = (row.Sku, row.Product);
                var values = optionValues[sku.Id].ToList();
                item.PriceCents = sku.PriceCents;
                item.Name = values.Count == 0 ? product.Name : $"{product.Name} ({string.Join(", ", values)})";
                item.ProductId = product.Id.ToString();
                item.CategoryId = product.CategoryId.ToString();
                item.Kind = product.Kind == ProductKinds.Physical ? "product" : "warranty_plan";
                item.Serialized = product.Serialized;
                item.Buyable = Visibility.IsBuyable(product, sku);
                if (product.LaunchId is { } launchId) item.Launch = new LaunchInfo { LaunchId = launchId.ToString(), Stage = product.LaunchStage ?? "" };
                item.EligibleProductIds.AddRange(eligible[product.Id]);
                item.CoverageMonths = product.CoverageMonths;
            }
            response.Items.Add(item);
        }
        metrics.GetPricing(Stopwatch.GetElapsedTime(started).TotalSeconds);
        return response;
    }

    public override async Task<GetSkuResponse> GetSku(GetSkuRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.SkuId, out var id)) throw CatalogErrors.NotFoundError("SKU");
        var sku = await db.Skus.AsNoTracking().Where(s => s.Id == id)
            .Join(db.Products, s => s.ProductId, p => p.Id, (s, p) => new GetSkuResponse
            { ProductId = p.Id.ToString(), Status = p.Status, Active = s.Active, LaunchId = s.LaunchId.ToString() ?? "" })
            .SingleOrDefaultAsync(context.CancellationToken);
        return sku ?? throw CatalogErrors.NotFoundError("SKU");
    }

    public override async Task<LockSkuForStockResponse> LockSkuForStock(LockSkuForStockRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.SkuId, out var id)) throw CatalogErrors.NotFoundError("SKU");
        // §6.8; the lock itself is the statement, so no transaction. Zero rows = no such SKU.
        var locked = await db.Database.ExecuteSqlAsync(
            $"UPDATE products SET serialized_locked = true WHERE id = (SELECT product_id FROM skus WHERE id = {id})", ct);
        if (locked == 0) throw CatalogErrors.NotFoundError("SKU");
        return await db.Skus.AsNoTracking().Where(s => s.Id == id)
            .Join(db.Products, s => s.ProductId, p => p.Id, (s, p) => new LockSkuForStockResponse { ProductId = p.Id.ToString(), Serialized = p.Serialized })
            .SingleAsync(ct);
    }
}
