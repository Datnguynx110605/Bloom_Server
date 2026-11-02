using System.Diagnostics;
using Bloom.Catalog.Caching;
using Bloom.Catalog.Data;
using Bloom.Catalog.Observability;
using Bloom.Catalog.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Category = Bloom.Catalog.V1.Category;
using CategoryRow = Bloom.Catalog.Data.Category;

namespace Bloom.Catalog.Services;

/// <summary>LLD 08-catalog §3 component <c>Public API</c>; anonymous callers allowed.</summary>
public sealed class PublicService(CatalogDbContext db, CatalogCache cache, CatalogMetrics metrics) : CatalogPublic.CatalogPublicBase
{
    public override async Task<ListCategoriesResponse> ListCategories(Empty request, ServerCallContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var tree = await cache.GetAsync(CatalogCache.CategoriesKey, ListCategoriesResponse.Parser);
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
            await cache.SetAsync(CatalogCache.CategoriesKey, tree);
        }
        metrics.Read("list_categories", hit, Stopwatch.GetElapsedTime(started).TotalSeconds);
        return tree;
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
