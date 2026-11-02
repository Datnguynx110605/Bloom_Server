using Bloom.Catalog.Caching;
using Bloom.Catalog.Context;
using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Storage;
using Bloom.Catalog.Validation;
using Bloom.Catalog.V1;
using Bloom.Shared.Concurrency;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Category = Bloom.Catalog.V1.Category;
using CategoryRow = Bloom.Catalog.Data.Category;

namespace Bloom.Catalog.Services;

/// <summary>LLD 08-catalog §3 component <c>Admin API</c>, split by area into partial files.</summary>
public sealed partial class AdminService(
    CatalogDbContext db, Audit audit, CacheInvalidation invalidation, ConcurrentUpdates concurrent,
    ProductDerived derived, MarkdownRenderer markdown, TimeProvider clock, ImageStore imageStore) : CatalogAdmin.CatalogAdminBase
{
    private const string SlugIndex = "ux_categories_slug";

    public override async Task<Category> CreateCategory(CreateCategoryRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var name = Inputs.Name(request.Name, "name", 100);
        var slug = Inputs.Slug(request.Slug);
        var ct = context.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var parentId = await ParentAsync(request.ParentId, self: null, ct);
        var row = new CategoryRow { Id = Guid.CreateVersion7(), ParentId = parentId, Name = name, Slug = slug, SortOrder = request.SortOrder };
        db.Categories.Add(row);
        audit.Record(caller, "catalog.category-created", "category", row.Id);
        invalidation.Categories();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (Db.IsUniqueViolation(e, SlugIndex)) { throw SlugTaken(); }
        await tx.CommitAsync(ct);
        return PublicService.ToProto(row);
    }

    public override async Task<Category> UpdateCategory(UpdateCategoryRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CatalogRoles);
        var name = Inputs.Name(request.Name, "name", 100);
        var slug = Inputs.Slug(request.Slug);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.Id, out var id)) throw CatalogErrors.NotFoundError("Category");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(id, ct) ?? throw CatalogErrors.NotFoundError("Category");
        var parentId = await ParentAsync(request.ParentId, self: row, ct);

        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();
        void Diff<T>(string field, T was, T now)
        {
            if (EqualityComparer<T>.Default.Equals(was, now)) return;
            before[field] = was;
            after[field] = now;
        }
        Diff("name", row.Name, name);
        Diff("slug", row.Slug, slug);
        Diff("sortOrder", row.SortOrder, request.SortOrder);
        Diff("parentId", row.ParentId, parentId);
        if (before.Count == 0) return PublicService.ToProto(row); // nothing to change: no write, no audit (R13)

        long version;
        try
        {
            version = await concurrent.UpdateVersionedAsync("category", db.Categories.Where(c => c.Id == id), request.Version,
                s => s.SetProperty(c => c.Name, name).SetProperty(c => c.Slug, slug)
                    .SetProperty(c => c.SortOrder, request.SortOrder).SetProperty(c => c.ParentId, parentId),
                CatalogErrors.NotFound, ct);
        }
        catch (PostgresException e) when (Db.IsUniqueViolation(e, SlugIndex)) { throw SlugTaken(); }

        if (before.ContainsKey("name")) // the category name is part of its products' search vectors (R12)
            await derived.RefreshAsync(await db.Products.Where(p => p.CategoryId == id).Select(p => p.Id).ToArrayAsync(ct));
        audit.Record(caller, "catalog.category-updated", "category", id, new { before, after });
        invalidation.Categories();
        if (before.ContainsKey("name") || before.ContainsKey("parentId")) invalidation.Listings(); // search vectors, category filter (R10)
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        row.Name = name;
        row.Slug = slug;
        row.SortOrder = request.SortOrder;
        row.ParentId = parentId;
        row.Version = version;
        return PublicService.ToProto(row);
    }

    /// <summary>
    /// The parent id for a category (null = top level), checked inside the caller's transaction. <paramref name="self"/> is the category being
    /// updated, null when creating. The parent row is locked, so a parent can't be moved under another category while a child is added to it.
    /// </summary>
    /// <exception cref="RpcException"><c>NOT_FOUND</c> for an unknown parent; <c>CATEGORY_DEPTH</c> for a third level (CAT-R01).</exception>
    private async Task<Guid?> ParentAsync(string rawParentId, CategoryRow? self, CancellationToken ct)
    {
        if (rawParentId == "") return null;
        if (!Guid.TryParse(rawParentId, out var parentId)) throw CatalogErrors.NotFoundError("Parent category");
        if (parentId == self?.Id) throw Depth("A category can't be its own parent.");

        var parent = await LockAsync(parentId, ct) ?? throw CatalogErrors.NotFoundError("Parent category");
        if (parent.ParentId is not null) throw Depth("The parent must be a top-level category.");
        if (self is not null && await db.Categories.AnyAsync(c => c.ParentId == self.Id, ct))
            throw Depth("A category with sub-categories can't get a parent.");
        return parentId;
    }

    private async Task<CategoryRow?> LockAsync(Guid id, CancellationToken ct) =>
        (await db.Categories.FromSql($"SELECT * FROM categories WHERE id = {id} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault();

    private static RpcException Depth(string message) => CatalogErrors.Error(CatalogErrors.CategoryDepth, message);

    private static RpcException SlugTaken() => CatalogErrors.Error(CatalogErrors.SlugTaken, "This slug is already in use.");
}
