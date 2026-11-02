using System.Diagnostics;
using Bloom.Catalog.Caching;
using Bloom.Catalog.Data;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Observability;
using Bloom.Catalog.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Category = Bloom.Catalog.V1.Category;
using CategoryRow = Bloom.Catalog.Data.Category;

namespace Bloom.Catalog.Services;

/// <summary>LLD 08-catalog §3 component <c>Public API</c>; anonymous callers allowed.</summary>
public sealed class PublicService(CatalogDbContext db, CatalogCache cache, CatalogMetrics metrics, ProductViews views) : CatalogPublic.CatalogPublicBase
{
    public override async Task<ListCategoriesResponse> ListCategories(Empty request, ServerCallContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var (tree, failed) = await cache.GetAsync(CatalogCache.CategoriesKey, ListCategoriesResponse.Parser);
        var hit = tree is not null;
        if (tree is null)
        {
            var rows = await db.Categories.AsNoTracking().ToListAsync(context.CancellationToken);
            var byParent = rows.Where(c => c.ParentId is not null).ToLookup(c => c.ParentId!.Value);
            tree = new ListCategoriesResponse
            {
                Categories =
                {
                    InOrder(rows.Where(c => c.ParentId is null)).Select(top =>
                    {
                        var category = ToProto(top);
                        category.Children.AddRange(InOrder(byParent[top.Id]).Select(ToProto));
                        return category;
                    }),
                },
            };
            if (!failed) await cache.SetAsync(CatalogCache.CategoriesKey, tree);
        }
        metrics.Read("list_categories", hit, Stopwatch.GetElapsedTime(started).TotalSeconds);
        return tree;
    }

    public override async Task<ProductView> GetProduct(GetProductRequest request, ServerCallContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var ct = context.CancellationToken;
        Guid? id = request.Id != ""
            ? Guid.TryParse(request.Id, out var parsed) ? parsed : null
            : await db.Products.AsNoTracking().Where(p => p.Slug == request.Slug).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);

        ProductView? view = null;
        var hit = false;
        if (id is { } productId)
        {
            var key = CatalogCache.ProductKey(productId);
            var (cached, failed) = await cache.GetAsync(key, ProductView.Parser);
            hit = cached is not null;
            view = cached ?? await views.LoadPublicAsync(productId, ct);
            if (view is not null && !hit && !failed) await cache.SetAsync(key, view);
        }
        metrics.Read("get_product", hit, Stopwatch.GetElapsedTime(started).TotalSeconds);
        return view ?? throw CatalogErrors.NotFoundError("Product");
    }

    internal static Category ToProto(CategoryRow c) => new()
    {
        Id = c.Id.ToString(),
        ParentId = c.ParentId?.ToString() ?? "",
        Name = c.Name,
        Slug = c.Slug,
        SortOrder = c.SortOrder,
        Version = c.Version,
    };

    private static IEnumerable<CategoryRow> InOrder(IEnumerable<CategoryRow> rows) =>
        rows.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id);
}
