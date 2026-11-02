using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;

namespace Bloom.Catalog.Tests;

/// <summary>Rows inserted straight into the host's database.</summary>
public static class Seed
{
    public static async Task<Category> CategoryAsync(TestCatalog t, Guid? parentId = null, string? name = null)
    {
        var id = Guid.CreateVersion7();
        var category = new Category { Id = id, ParentId = parentId, Name = name ?? $"Category {id:N}", Slug = $"c-{id:N}" };
        await using var db = t.NewDb();
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return category;
    }

    /// <summary>
    /// A product with option-less SKUs (default one at 99,900), inserted directly, then its derived columns are refreshed. Only the first SKU
    /// has the empty option key; the others get a throwaway key so the unique key allows them.
    /// </summary>
    public static async Task<(Product Product, Sku[] Skus)> ProductAsync(
        TestCatalog t, Guid categoryId, string status = "active", string kind = "physical", string? name = null,
        long[]? skuPrices = null, bool serializedLocked = false)
    {
        var id = Guid.CreateVersion7();
        var now = t.Clock.GetUtcNow();
        var product = new Product
        {
            Id = id, CategoryId = categoryId, Kind = kind, Name = name ?? $"Product {id:N}", Slug = $"p-{id:N}", Status = status,
            CoverageMonths = kind == ProductKinds.WarrantyPlan ? 12 : 0, SerializedLocked = serializedLocked,
            PublishedAt = status == ProductStatuses.Draft ? null : now, UpdatedAt = now,
        };
        var skus = (skuPrices ?? [99_900]).Select((price, i) => new Sku
        {
            Id = Guid.CreateVersion7(), ProductId = id, Code = $"SKU-{Guid.NewGuid():N}".ToUpperInvariant(), PriceCents = price,
            OptionKey = i == 0 ? "" : $"seed-{i}",
        }).ToArray();
        await using var db = t.NewDb();
        db.Products.Add(product);
        db.Skus.AddRange(skus);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new ProductDerived(db).RefreshAsync(id);
        return (product, skus);
    }
}
