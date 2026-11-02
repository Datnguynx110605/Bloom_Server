using Bloom.Catalog.Data;

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
}
