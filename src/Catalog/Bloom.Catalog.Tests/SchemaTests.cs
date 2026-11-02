using Bloom.Catalog.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Catalog.Tests;

public class SchemaTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Category NewCategory(string slug = "phones") => new() { Id = Guid.CreateVersion7(), Name = "Phones", Slug = slug };

    private static Product NewProduct(Guid categoryId, string slug = "iphone", string kind = ProductKinds.Physical) => new()
    {
        Id = Guid.CreateVersion7(),
        CategoryId = categoryId,
        Kind = kind,
        Name = "iPhone",
        Slug = slug,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Sku NewSku(Guid productId, string code = "IP-1", string optionKey = "", long priceCents = 100) => new()
    {
        Id = Guid.CreateVersion7(),
        ProductId = productId,
        Code = code,
        OptionKey = optionKey,
        PriceCents = priceCents,
    };

    private static Review NewReview(Guid productId, Guid? accountId, short rating = 5) => new()
    {
        Id = Guid.CreateVersion7(),
        ProductId = productId,
        AccountId = accountId,
        AuthorDisplay = "Ann L.",
        Rating = rating,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static T Tweak<T>(T entity, Action<T> change)
    {
        change(entity);
        return entity;
    }

    /// <summary>A category and a physical product, saved.</summary>
    private static async Task<Product> SeedProductAsync(CatalogDbContext db)
    {
        var category = NewCategory();
        var product = NewProduct(category.Id);
        db.AddRange(category, product);
        await db.SaveChangesAsync(Ct);
        return product;
    }

    private static async Task<string?> SqlStateOfAsync(CatalogDbContext db)
    {
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        return Assert.IsType<PostgresException>(e.InnerException).SqlState;
    }

    [Fact]
    public async Task Migration_applies_and_model_has_no_pending_changes()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        await using var db = t.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Theory, InlineData("ux_categories_slug"), InlineData("ux_products_slug"), InlineData("ux_skus_code"), InlineData("ux_skus_product_option_key")]
    public async Task Unique_constraints_are_named(string constraint)
    {
        await using var t = await TestCatalog.StartAsync(containers);
        Guid productId, otherId;
        await using (var db = t.NewDb())
        {
            productId = (await SeedProductAsync(db)).Id;
            var other = NewProduct((await db.Categories.SingleAsync(Ct)).Id, "ipad");
            db.Products.Add(other);
            await db.SaveChangesAsync(Ct);
            otherId = other.Id;
            db.Skus.Add(NewSku(productId));
            await db.SaveChangesAsync(Ct);
        }

        await using var dup = t.NewDb();
        var categoryId = (await dup.Categories.SingleAsync(Ct)).Id;
        switch (constraint)
        {
            case "ux_categories_slug": dup.Categories.Add(NewCategory()); break;
            case "ux_products_slug": dup.Products.Add(NewProduct(categoryId)); break;
            case "ux_skus_code": dup.Skus.Add(NewSku(otherId)); break;
            default: dup.Skus.Add(NewSku(productId, "IP-2")); break;
        }

        var e = await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync(Ct));
        Assert.True(Db.IsUniqueViolation(e, constraint));
        Assert.True(Db.IsUniqueViolation((PostgresException)e.InnerException!, constraint));
        Assert.False(Db.IsUniqueViolation(e, "other"));
    }

    [Fact]
    public async Task One_review_per_account_but_many_anonymous()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        await using var db = t.NewDb();
        var productId = (await SeedProductAsync(db)).Id;
        var account = Guid.CreateVersion7();
        db.Reviews.AddRange(NewReview(productId, null), NewReview(productId, null), NewReview(productId, account));
        await db.SaveChangesAsync(Ct);

        await using var dup = t.NewDb();
        dup.Reviews.Add(NewReview(productId, account));
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync(Ct));
        Assert.Equal("23505", Assert.IsType<PostgresException>(e.InnerException).SqlState);
        Assert.True(Db.IsUniqueViolation(e, "ux_reviews_product_account"));
    }

    [Fact]
    public async Task Check_constraints_reject_unknown_values()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        Guid categoryId, productId;
        await using (var db = t.NewDb())
        {
            var product = await SeedProductAsync(db);
            categoryId = product.CategoryId;
            productId = product.Id;
        }

        var bad = new Action<CatalogDbContext>[]
        {
            d => d.Products.Add(NewProduct(categoryId, "a", "gadget")),
            d => d.Products.Add(Tweak(NewProduct(categoryId, "b"), p => p.Status = "gone")),
            d => d.Products.Add(Tweak(NewProduct(categoryId, "c"), p => p.LaunchStage = "someday")),
            d => d.Skus.Add(NewSku(productId, priceCents: 0)),
            d => d.Reviews.Add(NewReview(productId, null, rating: 6)),
            d => d.Reviews.Add(NewReview(productId, null, rating: 0)),
            d => d.Reviews.Add(Tweak(NewReview(productId, null), r => r.Status = "gone")),
            d => d.ProductImages.Add(new ProductImage { Id = Guid.CreateVersion7(), ProductId = productId, StorageKey = "k", Status = "gone" }),
        };
        foreach (var add in bad)
        {
            await using var db = t.NewDb();
            add(db);
            Assert.Equal("23514", await SqlStateOfAsync(db));
        }
    }

    [Fact]
    public async Task Lld_indexes_exist()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        await using var conn = new NpgsqlConnection(t.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = 'public'", conn);
        var defs = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct)) defs.Add(reader.GetString(0));

        // (table, definition tail) of LLD 08-catalog §4.1.
        (string Table, string Tail)[] expected =
        [
            ("products", "USING gin (search_vector)"),
            ("products", "(category_id, status)"),
            ("products", "(min_price_cents)"),
            ("products", "(published_at)"),
            ("reviews", "(product_id, created_at)"),
            ("reviews", "(product_id, account_id) WHERE (account_id IS NOT NULL)"),
        ];
        foreach (var (table, tail) in expected)
            Assert.Contains(defs, d => d.Contains($" ON public.{table} ") && d.EndsWith(tail));
    }
}
