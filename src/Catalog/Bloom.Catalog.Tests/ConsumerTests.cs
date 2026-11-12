using Bloom.Catalog.Caching;
using Bloom.Catalog.Consumers;
using Bloom.Catalog.Data;
using Bloom.Identity.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Bloom.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Tests;

public class ConsumerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LaunchStageChangedV1 Launch(Guid launchId, string stage, Guid[] products, Sku[] skus) =>
        new(launchId, stage, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(30),
            [.. products.Select(p => new LaunchProductV1(p, 2))], [.. skus.Select(s => new LaunchSkuV1(s.Id, s.ProductId))]);

    private static Task StageAsync(TestCatalog t, LaunchStageChangedV1 e) =>
        Consume.Async<LaunchStageChangedConsumer>(t, LaunchMessages.StageChanged, e);

    private static Task<HttpResponseMessage> GetAsync(TestCatalog t, Guid id) => t.SendAsync(HttpMethod.Get, $"/api/catalog/products/{id}");

    private static async Task<Product> ProductRowAsync(TestCatalog t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Products.AsNoTracking().SingleAsync(p => p.Id == id, Ct);
    }

    private static async Task<bool> BuyableAsync(TestCatalog t, Guid sku) =>
        Assert.Single((await t.Internal.GetPricingAsync(new GetPricingRequest { SkuIds = { sku.ToString() } }, cancellationToken: Ct).ResponseAsync).Items).Buyable;

    [Fact]
    public async Task C06_announced_launch_sku_is_visible_but_not_buyable()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id, status: ProductStatuses.Draft);
        await (await GetAsync(t, phone.Product.Id)).ExpectErrorAsync(404, "NOT_FOUND");
        var launchId = Guid.NewGuid();

        await StageAsync(t, Launch(launchId, "announced", [phone.Product.Id], phone.Skus));

        var view = await (await GetAsync(t, phone.Product.Id)).OkJsonAsync();
        Assert.Equal(launchId.ToString(), view.GetProperty("launch").GetProperty("launchId").GetString());
        Assert.Equal("announced", view.GetProperty("launch").GetProperty("stage").GetString());
        Assert.False(await BuyableAsync(t, phone.Skus[0].Id));
        await using var db = t.NewDb();
        Assert.All(await db.Skus.AsNoTracking().Where(s => s.ProductId == phone.Product.Id).ToListAsync(Ct), s => Assert.Equal(launchId, s.LaunchId));
    }

    [Fact]
    public async Task C07_released_launch_makes_product_active_and_buyable()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id, status: ProductStatuses.Draft);
        var launchId = Guid.NewGuid();
        await StageAsync(t, Launch(launchId, "announced", [phone.Product.Id], phone.Skus));
        Assert.Null((await ProductRowAsync(t, phone.Product.Id)).PublishedAt);

        await StageAsync(t, Launch(launchId, "released", [phone.Product.Id], phone.Skus));

        var row = await ProductRowAsync(t, phone.Product.Id);
        Assert.Equal(ProductStatuses.Active, row.Status);
        Assert.NotNull(row.PublishedAt);
        Assert.True(await BuyableAsync(t, phone.Skus[0].Id));
        var view = await (await GetAsync(t, phone.Product.Id)).OkJsonAsync();
        Assert.Equal("released", view.GetProperty("launch").GetProperty("stage").GetString());
    }

    [Fact]
    public async Task Launch_draft_hides_product()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id);
        await (await GetAsync(t, phone.Product.Id)).OkJsonAsync();

        await StageAsync(t, Launch(Guid.NewGuid(), "draft", [phone.Product.Id], phone.Skus));

        await (await GetAsync(t, phone.Product.Id)).ExpectErrorAsync(404, "NOT_FOUND");
    }

    [Fact]
    public async Task Older_stage_after_newer_is_a_no_op()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id, status: ProductStatuses.Draft);
        var launchId = Guid.NewGuid();
        var released = Launch(launchId, "released", [phone.Product.Id], phone.Skus);
        await StageAsync(t, released);
        var before = await ProductRowAsync(t, phone.Product.Id);

        t.Clock.Advance(TimeSpan.FromMinutes(1)); // a write would now stamp a later updated_at
        await StageAsync(t, Launch(launchId, "announced", [phone.Product.Id], phone.Skus));
        t.Clock.Advance(TimeSpan.FromMinutes(1));
        await StageAsync(t, released); // the same event twice

        var after = await ProductRowAsync(t, phone.Product.Id);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(ProductStatuses.Active, after.Status);
        Assert.Equal("released", after.LaunchStage);
        Assert.Equal(before.PublishedAt, after.PublishedAt);
        Assert.Equal(before.Version, after.Version);
        Assert.True(await BuyableAsync(t, phone.Skus[0].Id));
    }

    [Fact]
    public async Task D36_launch_event_never_changes_archived_product()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var archived = await Seed.PhoneAsync(t, category.Id, status: ProductStatuses.Archived);
        var live = await Seed.PhoneAsync(t, category.Id, "Live phone", ProductStatuses.Draft);
        var before = await ProductRowAsync(t, archived.Product.Id);
        t.Clock.Advance(TimeSpan.FromMinutes(1)); // a write would now stamp a later updated_at
        var launchId = Guid.NewGuid();

        // one event over an archived and a draft product; no exception: the inbox records it as handled (no retry, no DLQ)
        await StageAsync(t, Launch(launchId, "released", [archived.Product.Id, live.Product.Id], [.. archived.Skus, .. live.Skus]));

        var after = await ProductRowAsync(t, archived.Product.Id);
        Assert.Equal(ProductStatuses.Archived, after.Status);
        Assert.Null(after.LaunchId);
        Assert.Null(after.LaunchStage);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.PublishedAt, after.PublishedAt);
        await using var db = t.NewDb();
        Assert.All(await db.Skus.AsNoTracking().Where(s => s.ProductId == archived.Product.Id).ToListAsync(Ct), s => Assert.Null(s.LaunchId)); // the whole product, SKUs included
        Assert.Equal(launchId, (await ProductRowAsync(t, live.Product.Id)).LaunchId); // the others in the same event are applied
        Assert.Equal(ProductStatuses.Active, (await ProductRowAsync(t, live.Product.Id)).Status);
    }

    [Fact]
    public async Task Launch_event_does_not_stamp_a_sku_under_a_product_it_does_not_belong_to()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var listed = await Seed.PhoneAsync(t, category.Id, "Listed");
        var other = await Seed.PhoneAsync(t, category.Id, "Other");

        // the event claims a SKU of `other` belongs to `listed`: the stamp needs the SKU's own product to be a listed one
        var wrong = new Sku { Id = other.Skus[0].Id, ProductId = listed.Product.Id, Code = other.Skus[0].Code };
        await StageAsync(t, Launch(Guid.NewGuid(), "announced", [listed.Product.Id], [.. listed.Skus, wrong]));

        await using var db = t.NewDb();
        Assert.All(await db.Skus.AsNoTracking().Where(s => s.ProductId == other.Product.Id).ToListAsync(Ct), s => Assert.Null(s.LaunchId));
    }

    [Fact]
    public async Task Launch_event_deletes_product_cache_after_commit()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id);
        await (await GetAsync(t, phone.Product.Id)).OkJsonAsync();
        var key = CatalogCache.ProductKey(phone.Product.Id);
        Assert.True(await t.Redis.KeyExistsAsync(key));
        var version = (long?)await t.Redis.StringGetAsync("catalog:version") ?? 0;

        await StageAsync(t, Launch(Guid.NewGuid(), "announced", [phone.Product.Id], phone.Skus));

        Assert.False(await t.Redis.KeyExistsAsync(key));
        Assert.Equal(version + 1, (long)await t.Redis.StringGetAsync("catalog:version"));
    }

    [Fact]
    public async Task Unknown_products_in_launch_event_are_skipped()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id, status: ProductStatuses.Draft);
        var launchId = Guid.NewGuid();
        var ghost = new Sku { Id = Guid.NewGuid(), ProductId = Guid.NewGuid(), Code = "GHOST" };

        await StageAsync(t, Launch(launchId, "announced", [phone.Product.Id, Guid.NewGuid()], [.. phone.Skus, ghost]));

        Assert.Equal("announced", (await ProductRowAsync(t, phone.Product.Id)).LaunchStage);
        await using var db = t.NewDb();
        Assert.Equal(0, await db.Products.CountAsync(p => p.LaunchId == launchId && p.Id != phone.Product.Id, Ct));
    }

    [Fact]
    public async Task Delivered_order_makes_buyer_eligible()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var phone = await Seed.PhoneAsync(t, category.Id);
        var (plan, _) = await Seed.PlanAsync(t, category.Id, [phone.Product.Id]);
        var account = Guid.NewGuid();
        var first = DateTimeOffset.UnixEpoch.AddDays(10);

        await Consume.Async<OrderDeliveredConsumer>(t, OrderMessages.Delivered, new OrderDeliveredV1(Guid.NewGuid(), account, [phone.Product.Id, plan.Id], first));
        await Consume.Async<OrderDeliveredConsumer>(t, OrderMessages.Delivered, new OrderDeliveredV1(Guid.NewGuid(), account, [phone.Product.Id], first.AddDays(5)));

        await using var db = t.NewDb();
        var row = await db.ReviewEligibilities.AsNoTracking().SingleAsync(e => e.AccountId == account, Ct);
        Assert.Equal(phone.Product.Id, row.ProductId);
        Assert.Equal(first, row.FirstDeliveredAt);
    }

    [Fact]
    public async Task Registration_and_contact_change_set_reviewer_name()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var account = Guid.NewGuid();

        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.CustomerRegistered, new CustomerRegisteredV1(account, "m@example.com", "Mary Jane Watson"));
        await using (var db = t.NewDb()) Assert.Equal("Mary W.", (await db.ReviewerNames.AsNoTracking().SingleAsync(n => n.AccountId == account, Ct)).DisplayName);

        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.ContactChanged, new ContactChangedV1(account, "m@example.com", "Mary Parker"));
        await using (var db = t.NewDb()) Assert.Equal("Mary P.", (await db.ReviewerNames.AsNoTracking().SingleAsync(n => n.AccountId == account, Ct)).DisplayName);
    }

    [Fact]
    public async Task D37_older_registered_event_does_not_overwrite_newer_contact_change()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var account = Guid.NewGuid();
        var t1 = t.Clock.GetUtcNow().UtcDateTime;
        var t2 = t1.AddMinutes(5);

        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.ContactChanged, new ContactChangedV1(account, "m@example.com", "Mary Parker"), occurredAt: t2);
        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.CustomerRegistered, new CustomerRegisteredV1(account, "m@example.com", "Mary Jane Watson"), occurredAt: t1);

        await using var db = t.NewDb();
        Assert.Equal("Mary P.", (await db.ReviewerNames.AsNoTracking().SingleAsync(n => n.AccountId == account, Ct)).DisplayName);
    }

    [Fact]
    public async Task D37_equal_timestamps_change_nothing_and_a_newer_event_wins()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var account = Guid.NewGuid();
        var at = t.Clock.GetUtcNow().UtcDateTime;
        async Task<string> NameAsync() { await using var db = t.NewDb(); return (await db.ReviewerNames.AsNoTracking().SingleAsync(n => n.AccountId == account, Ct)).DisplayName; }

        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.CustomerRegistered, new CustomerRegisteredV1(account, "m@example.com", "Mary Jane Watson"), occurredAt: at);
        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.ContactChanged, new ContactChangedV1(account, "m@example.com", "Mary Parker"), occurredAt: at);
        Assert.Equal("Mary W.", await NameAsync()); // equal is not newer: no change

        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.ContactChanged, new ContactChangedV1(account, "m@example.com", "Mary Parker"), occurredAt: at.AddSeconds(1));
        Assert.Equal("Mary P.", await NameAsync());
    }

    [Fact]
    public async Task Rename_keeps_old_reviews_and_names_new_ones()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var first = (await Seed.ProductAsync(t, category.Id)).Product;
        var second = (await Seed.ProductAsync(t, category.Id)).Product;
        var account = Guid.NewGuid();
        await Seed.EligibleAsync(t, account, first.Id);
        await Seed.EligibleAsync(t, account, second.Id);
        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.CustomerRegistered, new CustomerRegisteredV1(account, "m@example.com", "Mary Jane Watson"));
        var customer = TestCaller.Customer(account);
        await (await t.SendAsync(HttpMethod.Put, $"/api/reviews/{first.Id}", new { rating = 5 }, customer)).OkJsonAsync();

        await Consume.Async<ReviewerNameConsumer>(t, IdentityMessages.ContactChanged, new ContactChangedV1(account, "m@example.com", "Mary Parker"));
        var created = await (await t.SendAsync(HttpMethod.Put, $"/api/reviews/{second.Id}", new { rating = 4 }, customer)).OkJsonAsync();

        Assert.Equal("Mary P.", created.GetProperty("authorDisplay").GetString());
        await using var db = t.NewDb();
        Assert.Equal("Mary W.", (await db.Reviews.AsNoTracking().SingleAsync(r => r.ProductId == first.Id, Ct)).AuthorDisplay);
    }

    [Fact]
    public async Task C11_deleted_account_reviews_show_former_customer_and_still_count()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var product = (await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id)).Product;
        var account = Guid.NewGuid();
        var review = await Seed.ReviewAsync(t, product.Id, 4, accountId: account);
        await Seed.EligibleAsync(t, account, product.Id);
        await Seed.ReviewerNameAsync(t, account, "Ann L.");

        await Consume.Async<AccountDeletedConsumer>(t, IdentityMessages.AccountDeleted, new AccountDeletedV1(account));

        await using var db = t.NewDb();
        var row = await db.Reviews.AsNoTracking().SingleAsync(r => r.Id == review.Id, Ct);
        Assert.Equal("Former customer", row.AuthorDisplay);
        Assert.Null(row.AccountId);
        Assert.Equal(ReviewStatuses.Visible, row.Status);
        Assert.Equal(1, (await ProductRowAsync(t, product.Id)).RatingCount);
        Assert.False(await db.ReviewEligibilities.AnyAsync(e => e.AccountId == account, Ct));
        Assert.False(await db.ReviewerNames.AnyAsync(n => n.AccountId == account, Ct));
        var reviews = await (await t.SendAsync(HttpMethod.Get, $"/api/catalog/products/{product.Id}/reviews")).OkJsonAsync();
        Assert.Equal("Former customer", reviews.GetProperty("reviews")[0].GetProperty("authorDisplay").GetString());
    }

    [Fact]
    public async Task Invalid_payload_is_permanent_failure()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var phone = await Seed.PhoneAsync(t, (await Seed.CategoryAsync(t)).Id, status: ProductStatuses.Draft);

        await Assert.ThrowsAsync<PermanentFailureException>(() =>
            Consume.Async<LaunchStageChangedConsumer>(t, LaunchMessages.StageChanged, new { stage = "released", products = new[] { new { productId = phone.Product.Id, purchaseLimit = 1 } } }));

        var row = await ProductRowAsync(t, phone.Product.Id);
        Assert.Equal(ProductStatuses.Draft, row.Status);
        Assert.Null(row.LaunchStage);
    }
}
