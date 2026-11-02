using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Bloom.Catalog.Storage;
using Bloom.Catalog.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Image = Bloom.Catalog.V1.Image;
using OptionType = Bloom.Catalog.V1.OptionType;
using OptionValue = Bloom.Catalog.V1.OptionValue;

namespace Bloom.Catalog.Services;

/// <summary>Builds the public <see cref="ProductView"/> from Postgres (08-catalog §4, plan R11).</summary>
public sealed class ProductViews(CatalogDbContext db, ImageStore imageStore)
{
    /// <returns>Null when the product doesn't exist or isn't publicly visible (a draft, an archived product, a launch draft, a plan).</returns>
    public async Task<ProductView?> LoadPublicAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (p is null || !Visibility.IsPublic(p)) return null;

        var types = await db.OptionTypes.AsNoTracking().Where(t => t.ProductId == id).OrderBy(t => t.Position).ToListAsync(ct);
        var values = await db.OptionValues.AsNoTracking()
            .Where(v => db.OptionTypes.Any(t => t.Id == v.OptionTypeId && t.ProductId == id)).OrderBy(v => v.Position).ToListAsync(ct);
        var skus = await db.Skus.AsNoTracking().Where(s => s.ProductId == id && s.Active).OrderBy(s => s.Id).ToListAsync(ct);
        var links = await db.SkuOptionValues.AsNoTracking()
            .Where(l => db.Skus.Any(s => s.Id == l.SkuId && s.ProductId == id && s.Active)).ToListAsync(ct);
        var images = await db.ProductImages.AsNoTracking()
            .Where(i => i.ProductId == id && i.Status == ImageStatuses.Ready).OrderBy(i => i.Position).ThenBy(i => i.Id).ToListAsync(ct);
        var plans = await (from e in db.PlanEligibilities.AsNoTracking()
                           where e.EligibleProductId == id
                           join plan in db.Products.AsNoTracking() on e.PlanProductId equals plan.Id
                           join sku in db.Skus.AsNoTracking() on plan.Id equals sku.ProductId
                           orderby plan.Name, plan.Id
                           select new { plan, sku }).ToListAsync(ct);

        var typePosition = types.ToDictionary(t => t.Id, t => t.Position);
        var valueType = values.ToDictionary(v => v.Id, v => typePosition[v.OptionTypeId]);
        var view = new ProductView
        {
            Id = p.Id.ToString(), Slug = p.Slug, Name = p.Name, CategoryId = p.CategoryId.ToString(), DescriptionHtml = p.DescriptionHtml,
            RatingAverage = p.RatingCount == 0 ? 0 : (double)p.RatingSum / p.RatingCount, RatingCount = p.RatingCount,
        };
        if (p.PublishedAt is { } publishedAt) view.PublishedAt = Timestamp.FromDateTimeOffset(publishedAt);
        if (p.MinPriceCents is { } min) view.MinPriceCents = min;
        if (p.LaunchStage is { } stage) view.Launch = new V1.Launch { LaunchId = p.LaunchId?.ToString() ?? "", Stage = stage };
        view.Specs.AddRange(ProductSpecs.ToProto(p.Specs));
        view.Options.AddRange(types.Select(t => new OptionType
        {
            Id = t.Id.ToString(), Name = t.Name, Position = t.Position,
            Values = { values.Where(v => v.OptionTypeId == t.Id).Select(v => new OptionValue { Id = v.Id.ToString(), Value = v.Value, Position = v.Position }) },
        }));
        view.Skus.AddRange(skus.Select(s => new PublicSku
        {
            Id = s.Id.ToString(), Code = s.Code, PriceCents = s.PriceCents,
            OptionValueIds = { links.Where(l => l.SkuId == s.Id).OrderBy(l => valueType[l.OptionValueId]).Select(l => l.OptionValueId.ToString()) },
        }));
        view.Images.AddRange(images.Select(i => new Image
        {
            Id = i.Id.ToString(), Url = imageStore.PublicUrl(i.StorageKey), AltText = i.AltText ?? "", SkuId = i.SkuId?.ToString() ?? "", Position = i.Position,
        }));
        view.WarrantyPlans.AddRange(plans.Where(x => Visibility.IsShownPlan(x.plan, x.sku)).Select(x => new WarrantyPlan
        {
            ProductId = x.plan.Id.ToString(), Name = x.plan.Name, CoverageMonths = x.plan.CoverageMonths, SkuId = x.sku.Id.ToString(), PriceCents = x.sku.PriceCents,
        }));
        return view;
    }
}
