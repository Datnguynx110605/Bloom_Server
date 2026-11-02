using System.Text.Json;
using Bloom.Catalog.Context;
using Bloom.Catalog.Data;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Validation;
using Bloom.Catalog.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OptionType = Bloom.Catalog.V1.OptionType;
using OptionValue = Bloom.Catalog.V1.OptionValue;

namespace Bloom.Catalog.Services;

public sealed partial class AdminService
{
    private const string ProductSlugIndex = "ux_products_slug";

    /// <summary>The <c>specs</c> jsonb shape of 08-catalog §4.1: <c>[{group, items: [{label, value}]}]</c>.</summary>
    private sealed record SpecItemJson(string Label, string Value);

    private sealed record SpecGroupJson(string Group, List<SpecItemJson> Items);

    /// <summary>The kind-independent product fields after validation; <see cref="SpecsJson"/> is what goes into the jsonb column.</summary>
    private sealed record ProductFields(string Name, string Slug, string DescriptionMd, string DescriptionHtml, List<SpecGroupJson> Specs)
    {
        public string SpecsJson { get; } = JsonSerializer.Serialize(Specs, JsonSerializerOptions.Web);
    }

    public override async Task<AdminProduct> CreateProduct(CreateProductRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        if (!ProductKinds.All.Contains(request.Kind)) throw CatalogErrors.Validation("kind", "kind must be physical or warranty_plan.");
        var fields = Fields(request.Name, request.Slug, request.DescriptionMd, request.Specs);
        CheckKind(request.Kind, request.Serialized, request.CoverageMonths);
        var ct = context.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var categoryId = await RequireCategoryAsync(request.CategoryId, ct);
        var row = new Product
        {
            Id = Guid.CreateVersion7(), CategoryId = categoryId, Kind = request.Kind, Name = fields.Name, Slug = fields.Slug,
            DescriptionMd = fields.DescriptionMd, DescriptionHtml = fields.DescriptionHtml, Specs = fields.SpecsJson,
            Serialized = request.Serialized, CoverageMonths = request.CoverageMonths, UpdatedAt = clock.GetUtcNow(),
        };
        db.Products.Add(row);
        audit.Record(caller, "catalog.product-created", "product", row.Id);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (Db.IsUniqueViolation(e, ProductSlugIndex)) { throw SlugTaken(); }
        await derived.RefreshAsync(row.Id);
        await tx.CommitAsync(ct);
        return await LoadProductAsync(row.Id, ct); // a draft is invisible to the public: nothing to invalidate
    }

    public override async Task<AdminProduct> UpdateProduct(UpdateProductRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var fields = Fields(request.Name, request.Slug, request.DescriptionMd, request.Specs);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.Id, out var id)) throw CatalogErrors.NotFoundError("Product");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw CatalogErrors.NotFoundError("Product");
        CheckKind(row.Kind, request.Serialized, request.CoverageMonths);
        var categoryId = await RequireCategoryAsync(request.CategoryId, ct);

        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();
        void Diff<T>(string field, T was, T now, bool changed)
        {
            if (!changed) return;
            before[field] = was;
            after[field] = now;
        }
        Diff("categoryId", row.CategoryId, categoryId, row.CategoryId != categoryId);
        Diff("name", row.Name, fields.Name, row.Name != fields.Name);
        Diff("slug", row.Slug, fields.Slug, row.Slug != fields.Slug);
        Diff("descriptionMd", row.DescriptionMd, fields.DescriptionMd, row.DescriptionMd != fields.DescriptionMd);
        var wasSpecs = ParseSpecs(row.Specs);
        Diff("specs", wasSpecs, fields.Specs, JsonSerializer.Serialize(wasSpecs, JsonSerializerOptions.Web) != fields.SpecsJson);
        Diff("serialized", row.Serialized, request.Serialized, row.Serialized != request.Serialized);
        Diff("coverageMonths", row.CoverageMonths, request.CoverageMonths, row.CoverageMonths != request.CoverageMonths);
        if (before.Count == 0) return await LoadProductAsync(id, ct); // nothing to change: no write, no audit (R13)

        // CR-03 / 08-catalog §7: the flag only changes while no stock unit has locked it.
        if (row.Serialized != request.Serialized && !await concurrent.TryConditionalUpdateAsync("serialized_locked",
                $"UPDATE products SET serialized = {request.Serialized} WHERE id = {id} AND serialized_locked = false", ct))
            throw CatalogErrors.Error(CatalogErrors.SerializedLocked, "serialized can't change once stock has been received.");

        var now = clock.GetUtcNow();
        try
        {
            await concurrent.UpdateVersionedAsync("product", db.Products.Where(p => p.Id == id), request.Version,
                s => s.SetProperty(p => p.CategoryId, categoryId).SetProperty(p => p.Name, fields.Name).SetProperty(p => p.Slug, fields.Slug)
                    .SetProperty(p => p.DescriptionMd, fields.DescriptionMd).SetProperty(p => p.DescriptionHtml, fields.DescriptionHtml)
                    .SetProperty(p => p.Specs, fields.SpecsJson).SetProperty(p => p.Serialized, request.Serialized)
                    .SetProperty(p => p.CoverageMonths, request.CoverageMonths).SetProperty(p => p.UpdatedAt, now),
                CatalogErrors.NotFound, ct);
        }
        catch (PostgresException e) when (Db.IsUniqueViolation(e, ProductSlugIndex)) { throw SlugTaken(); }

        if (before.ContainsKey("name") || before.ContainsKey("categoryId") || before.ContainsKey("descriptionMd")) await derived.RefreshAsync(id);
        audit.Record(caller, "catalog.product-updated", "product", id, new { before, after });
        await InvalidateProductAsync(id, row.Kind == ProductKinds.WarrantyPlan, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadProductAsync(id, ct);
    }

    public override Task<AdminProduct> PublishProduct(LifecycleRequest request, ServerCallContext context) =>
        ChangeStatusAsync(request, context, ProductStatuses.Draft, ProductStatuses.Active, "catalog.product-published");

    public override Task<AdminProduct> ArchiveProduct(LifecycleRequest request, ServerCallContext context) =>
        ChangeStatusAsync(request, context, ProductStatuses.Active, ProductStatuses.Archived, "catalog.product-archived");

    public override Task<AdminProduct> ReactivateProduct(LifecycleRequest request, ServerCallContext context) =>
        ChangeStatusAsync(request, context, ProductStatuses.Archived, ProductStatuses.Active, "catalog.product-reactivated");

    /// <summary>R17: the one allowed transition <paramref name="from"/> → <paramref name="to"/>; publishing also needs an active SKU and sets <c>published_at</c> once.</summary>
    private async Task<AdminProduct> ChangeStatusAsync(LifecycleRequest request, ServerCallContext context, string from, string to, string action)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.Id, out var id)) throw CatalogErrors.NotFoundError("Product");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var product = await LockProductAsync(id, ct);
        if (product.Status != from) throw CatalogErrors.Error(CatalogErrors.InvalidLifecycle, $"The product is {product.Status}; this action needs {from}.");
        if (to == ProductStatuses.Active && from == ProductStatuses.Draft && !await db.Skus.AnyAsync(s => s.ProductId == id && s.Active, ct))
            throw CatalogErrors.Error(CatalogErrors.PublishIncomplete, "Publishing needs at least one active SKU.");

        var now = clock.GetUtcNow();
        await concurrent.UpdateVersionedAsync("product", db.Products.Where(p => p.Id == id), request.Version,
            s => s.SetProperty(p => p.Status, to).SetProperty(p => p.PublishedAt, product.PublishedAt ?? now).SetProperty(p => p.UpdatedAt, now),
            CatalogErrors.NotFound, ct);
        audit.Record(caller, action, "product", id, new { before = new { status = from }, after = new { status = to } });
        await InvalidateProductAsync(id, product.Kind == ProductKinds.WarrantyPlan, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadProductAsync(id, ct);
    }

    public override async Task<AdminProduct> AdminGetProduct(AdminGetProductRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.CatalogRoles);
        return Guid.TryParse(request.Id, out var id)
            ? await LoadProductAsync(id, context.CancellationToken)
            : throw CatalogErrors.NotFoundError("Product");
    }

    public override async Task<AdminListProductsResponse> AdminListProducts(AdminListProductsRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var (page, size) = Inputs.Paging(request.Page, request.PageSize, 50, 100);
        if (request.Status != "" && !ProductStatuses.All.Contains(request.Status))
            throw CatalogErrors.Validation("status", "status must be draft, active or archived.");
        Guid? categoryId = null;
        if (request.CategoryId != "")
            categoryId = Guid.TryParse(request.CategoryId, out var parsed) ? parsed : throw CatalogErrors.Validation("category_id", "category_id must be a UUID.");
        var ct = context.CancellationToken;

        var query = db.Products.AsNoTracking();
        if (request.Status != "") query = query.Where(p => p.Status == request.Status);
        if (categoryId is not null) query = query.Where(p => p.CategoryId == categoryId);
        if (request.Q.Trim() is { Length: > 0 } text)
        {
            var pattern = $"%{text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%"; // contains, wildcards literal
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Slug, pattern)
                || db.Skus.Any(s => s.ProductId == p.Id && EF.Functions.ILike(s.Code, pattern)));
        }

        var rows = await query.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Id)
            .Skip((int)Math.Min((long)(page - 1) * size, int.MaxValue)).Take(size).ToListAsync(ct);
        var response = new AdminListProductsResponse { Total = await query.CountAsync(ct) };
        response.Items.AddRange(rows.Select(p =>
        {
            var item = new AdminProductSummary
            {
                Id = p.Id.ToString(), Slug = p.Slug, Name = p.Name, Kind = p.Kind, Status = p.Status, CategoryId = p.CategoryId.ToString(),
                LaunchStage = p.LaunchStage ?? "", Version = p.Version, UpdatedAt = Timestamp.FromDateTimeOffset(p.UpdatedAt),
            };
            if (p.MinPriceCents is { } min) item.MinPriceCents = min;
            return item;
        }));
        return response;
    }

    /// <summary>Trimmed and checked name, slug, description (with its HTML) and specs, in the order plan R14 lists them.</summary>
    private ProductFields Fields(string name, string slug, string descriptionMd, IEnumerable<SpecGroup> specs)
    {
        var description = Inputs.OptionalText(descriptionMd, "description_md", 20_000) ?? "";
        var groups = specs.Select(g =>
        {
            var items = g.Items.Select(i => new SpecItemJson(i.Label.Trim(), i.Value.Trim())).ToList();
            return new SpecGroupJson(g.Group.Trim(), items);
        }).ToList();
        if (groups.Any(g => g.Group == "" || g.Items.Any(i => i.Label == "" || i.Value == "")))
            throw CatalogErrors.Validation("specs", "Every spec group, label and value must be non-empty.");
        return new ProductFields(Inputs.Name(name, "name", 200), Inputs.Slug(slug), description, markdown.ToHtml(description), groups);
    }

    /// <summary>Plan R14: a plan is never serialized and covers 1 to 120 months; a physical product has no coverage.</summary>
    private static void CheckKind(string kind, bool serialized, int coverageMonths)
    {
        if (kind == ProductKinds.WarrantyPlan)
        {
            if (serialized) throw CatalogErrors.Validation("serialized", "A warranty plan can't be serialized.");
            if (coverageMonths is < 1 or > 120) throw CatalogErrors.Validation("coverage_months", "coverage_months must be 1 to 120 for a warranty plan.");
        }
        else if (coverageMonths != 0)
        {
            throw CatalogErrors.Validation("coverage_months", "coverage_months must be 0 for a physical product.");
        }
    }

    /// <exception cref="RpcException"><c>NOT_FOUND</c> for a malformed or unknown category id.</exception>
    private async Task<Guid> RequireCategoryAsync(string rawId, CancellationToken ct) =>
        Guid.TryParse(rawId, out var id) && await db.Categories.AnyAsync(c => c.Id == id, ct) ? id : throw CatalogErrors.NotFoundError("Category");

    private static List<SpecGroupJson> ParseSpecs(string json) =>
        JsonSerializer.Deserialize<List<SpecGroupJson>>(json, JsonSerializerOptions.Web) ?? [];

    /// <summary>The whole product as staff see it: every SKU and image, Markdown and HTML, plan eligibility.</summary>
    private async Task<AdminProduct> LoadProductAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw CatalogErrors.NotFoundError("Product");
        var types = await db.OptionTypes.AsNoTracking().Where(t => t.ProductId == id).OrderBy(t => t.Position).ToListAsync(ct);
        var values = await db.OptionValues.AsNoTracking()
            .Where(v => db.OptionTypes.Any(t => t.Id == v.OptionTypeId && t.ProductId == id)).OrderBy(v => v.Position).ToListAsync(ct);
        var skus = await db.Skus.AsNoTracking().Where(s => s.ProductId == id).OrderBy(s => s.Id).ToListAsync(ct);
        var links = await db.SkuOptionValues.AsNoTracking()
            .Where(l => db.Skus.Any(s => s.Id == l.SkuId && s.ProductId == id)).ToListAsync(ct);
        var images = await db.ProductImages.AsNoTracking().Where(i => i.ProductId == id).OrderBy(i => i.Position).ThenBy(i => i.Id).ToListAsync(ct);
        var eligible = await db.PlanEligibilities.AsNoTracking().Where(e => e.PlanProductId == id).Select(e => e.EligibleProductId).ToListAsync(ct);

        var typePosition = types.ToDictionary(t => t.Id, t => t.Position);
        var valueType = values.ToDictionary(v => v.Id, v => typePosition[v.OptionTypeId]);
        var result = new AdminProduct
        {
            Id = p.Id.ToString(), CategoryId = p.CategoryId.ToString(), Kind = p.Kind, Name = p.Name, Slug = p.Slug,
            DescriptionMd = p.DescriptionMd, DescriptionHtml = p.DescriptionHtml, Serialized = p.Serialized,
            SerializedLocked = p.SerializedLocked, CoverageMonths = p.CoverageMonths, Status = p.Status,
            LaunchId = p.LaunchId?.ToString() ?? "", LaunchStage = p.LaunchStage ?? "", RatingSum = p.RatingSum,
            RatingCount = p.RatingCount, Version = p.Version, UpdatedAt = Timestamp.FromDateTimeOffset(p.UpdatedAt),
        };
        if (p.PublishedAt is { } publishedAt) result.PublishedAt = Timestamp.FromDateTimeOffset(publishedAt);
        if (p.MinPriceCents is { } min) result.MinPriceCents = min;
        result.Specs.AddRange(ParseSpecs(p.Specs).Select(g =>
            new SpecGroup { Group = g.Group, Items = { g.Items.Select(i => new SpecItem { Label = i.Label, Value = i.Value }) } }));
        result.Options.AddRange(types.Select(t => new OptionType
        {
            Id = t.Id.ToString(), Name = t.Name, Position = t.Position,
            Values = { values.Where(v => v.OptionTypeId == t.Id).Select(v => new OptionValue { Id = v.Id.ToString(), Value = v.Value, Position = v.Position }) },
        }));
        result.Skus.AddRange(skus.Select(s =>
            ToProto(s, links.Where(l => l.SkuId == s.Id).OrderBy(l => valueType[l.OptionValueId]).Select(l => l.OptionValueId))));
        result.Images.AddRange(images.Select(i => new Image
        {
            Id = i.Id.ToString(), Url = $"{options.Storage.PublicBaseUrl}/{i.StorageKey}", AltText = i.AltText ?? "", SkuId = i.SkuId?.ToString() ?? "",
            Position = i.Position, Status = i.Status,
        }));
        result.EligibleProductIds.AddRange(eligible.Select(e => e.ToString()));
        return result;
    }
}
