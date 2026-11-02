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

    /// <summary>
    /// A physical product with option types Storage {256 GB, 512 GB} and Color {Black, White} and the four SKUs (256/Black 99,900,
    /// 256/White 99,900, 512/Black 119,900, 512/White 119,900), inserted directly; derived columns refreshed.
    /// </summary>
    public static async Task<SeededPhone> PhoneAsync(TestCatalog t, Guid categoryId, string name = "Bloom Phone Pro Max", string status = "active")
    {
        var id = Guid.CreateVersion7();
        var now = t.Clock.GetUtcNow();
        var product = new Product
        {
            Id = id, CategoryId = categoryId, Kind = ProductKinds.Physical, Name = name, Slug = $"p-{id:N}", Status = status,
            PublishedAt = status == ProductStatuses.Draft ? null : now, UpdatedAt = now,
        };
        var storage = new OptionType { Id = Guid.CreateVersion7(), ProductId = id, Name = "Storage", Position = 0 };
        var color = new OptionType { Id = Guid.CreateVersion7(), ProductId = id, Name = "Color", Position = 1 };
        OptionValue Value(OptionType type, string text, int position) =>
            new() { Id = Guid.CreateVersion7(), OptionTypeId = type.Id, Value = text, Position = position };
        OptionValue s256 = Value(storage, "256 GB", 0), s512 = Value(storage, "512 GB", 1), black = Value(color, "Black", 0), white = Value(color, "White", 1);

        var combos = new (OptionValue Storage, OptionValue Color, long Price)[]
            { (s256, black, 99_900), (s256, white, 99_900), (s512, black, 119_900), (s512, white, 119_900) };
        var skus = combos.Select(c => new Sku
        {
            Id = Guid.CreateVersion7(), ProductId = id, Code = $"PH-{Guid.NewGuid():N}".ToUpperInvariant(), PriceCents = c.Price,
            OptionKey = string.Join(',', new[] { c.Storage.Id.ToString("D"), c.Color.Id.ToString("D") }.Order(StringComparer.Ordinal)),
        }).ToArray();

        await using var db = t.NewDb();
        db.Products.Add(product);
        db.OptionTypes.AddRange(storage, color);
        db.OptionValues.AddRange(s256, s512, black, white);
        db.Skus.AddRange(skus);
        db.SkuOptionValues.AddRange(combos.Select((c, i) => new[]
            { new SkuOptionValue { SkuId = skus[i].Id, OptionValueId = c.Storage.Id }, new SkuOptionValue { SkuId = skus[i].Id, OptionValueId = c.Color.Id } }).SelectMany(x => x));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new ProductDerived(db).RefreshAsync(id);
        return new SeededPhone(product, skus, s256.Id, s512.Id, black.Id, white.Id,
            combos.Select((c, i) => (c.Storage.Id, c.Color.Id, skus[i])).ToArray());
    }

    /// <summary>A warranty plan with one option-less SKU, eligible for the given devices.</summary>
    public static async Task<(Product Plan, Sku Sku)> PlanAsync(
        TestCatalog t, Guid categoryId, Guid[] eligible, long price = 9_900, int coverageMonths = 24, string status = "active")
    {
        var (plan, skus) = await ProductAsync(t, categoryId, status, ProductKinds.WarrantyPlan, skuPrices: [price]);
        plan.CoverageMonths = coverageMonths;
        await using var db = t.NewDb();
        db.Products.Attach(plan).Property(p => p.CoverageMonths).IsModified = true;
        db.PlanEligibilities.AddRange(eligible.Select(e => new PlanEligibility { PlanProductId = plan.Id, EligibleProductId = e }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (plan, skus[0]);
    }
}

public sealed record SeededPhone(Product Product, Sku[] Skus, Guid Storage256, Guid Storage512, Guid Black, Guid White, (Guid Storage, Guid Color, Sku Sku)[] Combos)
{
    public Sku Sku(Guid storage, Guid color) => Combos.Single(c => c.Storage == storage && c.Color == color).Sku;
}
