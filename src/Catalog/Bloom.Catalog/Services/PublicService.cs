using System.Diagnostics;
using Bloom.Catalog.Caching;
using Bloom.Catalog.Context;
using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Observability;
using Bloom.Catalog.Storage;
using Bloom.Catalog.Validation;
using Bloom.Catalog.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Category = Bloom.Catalog.V1.Category;
using CategoryRow = Bloom.Catalog.Data.Category;

namespace Bloom.Catalog.Services;

/// <summary>LLD 08-catalog §3 component <c>Public API</c>; anonymous callers allowed.</summary>
public sealed class PublicService(CatalogDbContext db, CatalogCache cache, CatalogMetrics metrics, ProductViews views, ImageStore images) : CatalogPublic.CatalogPublicBase
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

    private const int ReviewPageSize = 20;

    public override async Task<ListReviewsResponse> ListReviews(ListReviewsRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var (page, size) = Inputs.Paging(request.Page, 0, ReviewPageSize, ReviewPageSize);
        if (!Guid.TryParse(request.ProductId, out var productId)) throw CatalogErrors.NotFoundError("Product");
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null || !Visibility.IsPublic(product)) throw CatalogErrors.NotFoundError("Product");

        var visible = db.Reviews.AsNoTracking().Where(r => r.ProductId == productId && r.Status == ReviewStatuses.Visible);
        var rows = await visible.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((int)Math.Min((long)(page - 1) * size, int.MaxValue)).Take(size).ToListAsync(ct);
        var response = new ListReviewsResponse { Total = await visible.CountAsync(ct) };
        response.Reviews.AddRange(rows.Select(CustomerService.ToProto));
        // The caller's own review, whatever its status, so they can edit it (owner item 5); anonymous and staff callers get none.
        if (Caller.TryFrom(context) is { UserType: "customer" } caller
            && await db.Reviews.AsNoTracking().SingleOrDefaultAsync(r => r.ProductId == productId && r.AccountId == caller.UserId, ct) is { } mine)
            response.MyReview = CustomerService.ToProto(mine);
        return response;
    }

    private static readonly string[] Sorts = ["relevance", "price_asc", "price_desc", "newest", "rating"];

    public override async Task<SearchProductsResponse> SearchProducts(SearchProductsRequest request, ServerCallContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var ct = context.CancellationToken;
        var (page, size) = Inputs.Paging(request.Page, request.PageSize, 24, 48);
        var sort = request.Sort == "" ? "relevance" : request.Sort;
        if (!Sorts.Contains(sort)) throw CatalogErrors.Validation("sort", $"sort must be one of {string.Join(", ", Sorts)}.");
        var categoryId = request.CategoryId == "" ? (Guid?)null
            : Guid.TryParse(request.CategoryId, out var parsed) ? parsed : throw CatalogErrors.Validation("category_id", "category_id must be a UUID.");
        var optionIds = request.OptionValueIds
            .Select(id => Guid.TryParse(id, out var g) ? g : throw CatalogErrors.Validation("option_value_ids", "option_value_ids must be UUIDs."))
            .Distinct().Order().ToArray();

        // The canonical request is the cache key: defaults applied, ids sorted.
        var canonical = new SearchProductsRequest
        {
            Q = request.Q.Trim(), CategoryId = categoryId?.ToString() ?? "", Sort = sort, Page = page, PageSize = size,
            OptionValueIds = { optionIds.Select(g => g.ToString()) },
        };
        if (request.HasPriceMin) canonical.PriceMin = request.PriceMin;
        if (request.HasPriceMax) canonical.PriceMax = request.PriceMax;

        var version = await cache.CatalogVersionAsync();
        var key = version is { } v ? CatalogCache.SearchKey(v, canonical) : null;
        var (result, failed) = key is null ? new CacheRead<SearchProductsResponse>(null, true) : await cache.GetAsync(key, SearchProductsResponse.Parser);
        var hit = result is not null;
        if (result is null)
        {
            var queryStarted = Stopwatch.GetTimestamp();
            result = await QueryAsync(canonical, categoryId, optionIds, ct);
            metrics.Search(Stopwatch.GetElapsedTime(queryStarted).TotalSeconds);
            if (!failed) await cache.SetAsync(key!, result);
        }
        metrics.Read("search_products", hit, Stopwatch.GetElapsedTime(started).TotalSeconds);
        return result;
    }

    private sealed class CardRow
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public long? MinPriceCents { get; set; }
        public int RatingSum { get; set; }
        public int RatingCount { get; set; }
        public string? ImageKey { get; set; }
        public string? LaunchStage { get; set; }
    }

    /// <summary>08-catalog §6.2 and plan R19; <paramref name="r"/> is the canonical request.</summary>
    private async Task<SearchProductsResponse> QueryAsync(SearchProductsRequest r, Guid? categoryId, Guid[] optionIds, CancellationToken ct)
    {
        var result = new SearchProductsResponse { Page = r.Page, PageSize = r.PageSize };

        // Option values group by type name, case-insensitively, across products (owner item 4); an unknown id can match nothing.
        var chosen = optionIds.Length == 0 ? [] : await (
            from v in db.OptionValues.AsNoTracking() where optionIds.Contains(v.Id)
            join t in db.OptionTypes.AsNoTracking() on v.OptionTypeId equals t.Id
            select new { v.Id, t.Name }).ToListAsync(ct);
        if (chosen.Count != optionIds.Length) return result;

        var args = new List<object>();
        string P(object value) { args.Add(value); return $"{{{args.Count - 1}}}"; }

        var where = new List<string> { Visibility.PublicProductSql };
        string? tsquery = null;
        if (r.Q != "")
        {
            tsquery = $"websearch_to_tsquery('english', {P(r.Q)})";
            where.Add($"(numnode({tsquery}) = 0 OR p.search_vector @@ {tsquery})"); // a query of only stop words filters nothing (§8)
        }
        if (categoryId is { } category)
        {
            var id = P(category);
            where.Add($"p.category_id IN (SELECT c.id FROM categories c WHERE c.id = {id} OR c.parent_id = {id})");
        }
        if (r.HasPriceMin) where.Add($"p.min_price_cents >= {P(r.PriceMin)}");
        if (r.HasPriceMax) where.Add($"p.min_price_cents <= {P(r.PriceMax)}");
        if (chosen.Count > 0)
            where.Add("EXISTS (SELECT 1 FROM skus s WHERE s.product_id = p.id AND s.active " + string.Concat(
                chosen.GroupBy(c => c.Name.ToLowerInvariant()).Select(g =>
                    $"AND EXISTS (SELECT 1 FROM sku_option_values o WHERE o.sku_id = s.id AND o.option_value_id = ANY({P(g.Select(c => c.Id).ToArray())})) ")) + ")");

        var order = r.Sort switch
        {
            "price_asc" => "p.min_price_cents ASC NULLS LAST",
            "price_desc" => "p.min_price_cents DESC NULLS LAST",
            "rating" => "p.rating_sum::numeric / NULLIF(p.rating_count, 0) DESC NULLS LAST, p.rating_count DESC",
            "relevance" when tsquery is not null => $"ts_rank_cd(p.search_vector, {tsquery}) DESC, p.published_at DESC NULLS LAST",
            _ => "p.published_at DESC NULLS LAST", // newest; relevance without text
        };
        var filter = string.Join(" AND ", where);

        // Only constants and numbered placeholders are concatenated into the SQL; every value travels as a parameter.
        var countSql = $"SELECT count(*)::int AS \"Value\" FROM products p WHERE {filter}";
        result.Total = await db.Database.SqlQueryRaw<int>(countSql, [.. args]).SingleAsync(ct);
        var pageSql = $"""
            SELECT p.id AS "Id", p.slug AS "Slug", p.name AS "Name", p.min_price_cents AS "MinPriceCents", p.rating_sum AS "RatingSum",
              p.rating_count AS "RatingCount", p.launch_stage AS "LaunchStage",
              (SELECT i.storage_key FROM product_images i WHERE i.product_id = p.id AND i.status = 'ready' ORDER BY i.position, i.id LIMIT 1) AS "ImageKey"
            FROM products p WHERE {filter}
            ORDER BY {order}, p.id
            LIMIT {P(r.PageSize)} OFFSET {P((long)(r.Page - 1) * r.PageSize)}
            """;
        var rows = await db.Database.SqlQueryRaw<CardRow>(pageSql, [.. args]).ToListAsync(ct);

        result.Items.AddRange(rows.Select(c =>
        {
            var card = new ProductCard
            {
                Id = c.Id.ToString(), Slug = c.Slug, Name = c.Name, RatingCount = c.RatingCount,
                RatingAverage = c.RatingCount == 0 ? 0 : (double)c.RatingSum / c.RatingCount,
                ImageUrl = c.ImageKey is null ? "" : images.PublicUrl(c.ImageKey), LaunchStage = c.LaunchStage ?? "",
            };
            if (c.MinPriceCents is { } min) card.MinPriceCents = min;
            return card;
        }));
        return result;
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
