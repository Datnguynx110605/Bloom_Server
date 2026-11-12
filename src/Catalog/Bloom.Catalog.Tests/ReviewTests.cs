using System.Net;
using System.Text.Json;
using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Tests;

public class ReviewTests(Containers containers)
{
    private const string Admin = "/api/admin/catalog";
    private static readonly TestCaller Support = TestCaller.Staff("support");

    private static Task<HttpResponseMessage> WriteAsync(TestCatalog t, Guid product, Guid account, object body) =>
        t.SendAsync(HttpMethod.Put, $"/api/reviews/{product}", body, TestCaller.Customer(account));

    private static Task<HttpResponseMessage> DeleteAsync(TestCatalog t, Guid product, Guid account) =>
        t.SendAsync(HttpMethod.Delete, $"/api/reviews/{product}", caller: TestCaller.Customer(account));

    private static Task<HttpResponseMessage> ListAsync(TestCatalog t, Guid product, string query = "", TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Get, $"/api/catalog/products/{product}/reviews{query}", caller: caller);

    private static Task<HttpResponseMessage> ModerateAsync(TestCatalog t, string action, Guid review, string reason = "spam", TestCaller? caller = null) =>
        t.SendAsync(HttpMethod.Post, $"{Admin}/reviews/{review}/{action}", new { reason }, caller ?? Support);

    private static async Task<JsonElement> PageAsync(TestCatalog t, Guid product) =>
        await (await t.SendAsync(HttpMethod.Get, $"/api/catalog/products/{product}")).OkJsonAsync();

    /// <summary>A product with an eligible customer (and optionally a "First L." name).</summary>
    private async Task<(TestCatalog T, Product Product, Guid Account)> StartAsync(string? name = "Ann L.")
    {
        var t = await TestCatalog.StartAsync(containers);
        var product = (await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id)).Product;
        var account = Guid.NewGuid();
        await Seed.EligibleAsync(t, account, product.Id);
        if (name is not null) await Seed.ReviewerNameAsync(t, account, name);
        return (t, product, account);
    }

    private static async Task<(int Sum, int Count)> RatingAsync(TestCatalog t, Guid product)
    {
        await using var db = t.NewDb();
        var p = await db.Products.AsNoTracking().SingleAsync(x => x.Id == product, TestContext.Current.CancellationToken);
        return (p.RatingSum, p.RatingCount);
    }

    /// <summary>The aggregates equal a recount of the visible reviews.</summary>
    private static async Task AssertRecountAsync(TestCatalog t, Guid product)
    {
        await using var db = t.NewDb();
        var ratings = await db.Reviews.Where(r => r.ProductId == product && r.Status == ReviewStatuses.Visible).Select(r => (int)r.Rating)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal((ratings.Sum(), ratings.Count), await RatingAsync(t, product));
    }

    private static async Task<string?> FieldAsync(HttpResponseMessage response) =>
        (await response.ExpectErrorAsync(400, "VALIDATION_FAILED")).GetProperty("metadata").GetProperty("field").GetString();

    [Fact]
    public async Task Verified_buyer_writes_a_review()
    {
        var (t, product, account) = await StartAsync();
        await using var host = t;
        using var metrics = new MetricCapture(t.Services);

        var review = await (await WriteAsync(t, product.Id, account, new { rating = 5, text = "Great phone", authorDisplay = "ignored" })).OkJsonAsync();

        Assert.Equal("Ann L.", review.GetProperty("authorDisplay").GetString());
        Assert.Equal("1", review.GetProperty("version").GetString());
        Assert.Equal(5, review.GetProperty("rating").GetInt32());
        Assert.Equal("Great phone", review.GetProperty("text").GetString());
        Assert.Equal(product.Id.ToString(), review.GetProperty("productId").GetString());
        Assert.Equal((5, 1), await RatingAsync(t, product.Id));
        Assert.Equal(1, metrics.Sum("reviews_total", ("action", "create")));
        Assert.Empty(await Outbox.AuditAsync(t)); // customer actions aren't audited (R13)
    }

    [Fact]
    public async Task C08_review_without_delivery_is_NOT_VERIFIED_BUYER()
    {
        var (t, product, _) = await StartAsync();
        await using var host = t;

        await (await WriteAsync(t, product.Id, Guid.NewGuid(), new { rating = 5 })).ExpectErrorAsync(403, "NOT_VERIFIED_BUYER");

        Assert.Equal((0, 0), await RatingAsync(t, product.Id));
    }

    [Fact]
    public async Task C09_second_review_on_same_product_is_rejected()
    {
        var (t, product, account) = await StartAsync();
        await using var host = t;
        await (await WriteAsync(t, product.Id, account, new { rating = 4 })).OkJsonAsync();

        await (await WriteAsync(t, product.Id, account, new { rating = 1 })).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");

        Assert.Equal((4, 1), await RatingAsync(t, product.Id));
    }

    [Fact]
    public async Task Concurrent_creates_leave_one_review()
    {
        var (t, product, account) = await StartAsync();
        await using var host = t;

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => WriteAsync(t, product.Id, account, new { rating = 3 })));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        await responses.Single(r => r.StatusCode == HttpStatusCode.Conflict).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");
        Assert.Equal((3, 1), await RatingAsync(t, product.Id));
        await using var db = t.NewDb();
        Assert.Equal(1, await db.Reviews.CountAsync(r => r.ProductId == product.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Edit_with_version()
    {
        var (t, product, account) = await StartAsync();
        await using var host = t;
        using var metrics = new MetricCapture(t.Services);
        await (await WriteAsync(t, product.Id, account, new { rating = 4, text = "ok" })).OkJsonAsync();

        var edited = await (await WriteAsync(t, product.Id, account, new { rating = 2, text = "meh", version = 1 })).OkJsonAsync();

        Assert.Equal("2", edited.GetProperty("version").GetString());
        Assert.Equal(2, edited.GetProperty("rating").GetInt32());
        Assert.Equal("Ann L.", edited.GetProperty("authorDisplay").GetString());
        Assert.Equal((2, 1), await RatingAsync(t, product.Id));
        Assert.Equal(1, metrics.Sum("reviews_total", ("action", "edit")));

        await (await WriteAsync(t, product.Id, account, new { rating = 5, version = 1 })).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT"); // stale
        await (await WriteAsync(t, product.Id, Guid.NewGuid(), new { rating = 5, version = 1 })).ExpectErrorAsync(404, "NOT_FOUND"); // no review

        // Identical rating and text: nothing written, the cached page stays.
        await PageAsync(t, product.Id);
        Assert.True(await t.Redis.KeyExistsAsync($"product:{product.Id}"));
        var same = await (await WriteAsync(t, product.Id, account, new { rating = 2, text = "meh", version = 2 })).OkJsonAsync();
        Assert.Equal("2", same.GetProperty("version").GetString());
        Assert.Equal((2, 1), await RatingAsync(t, product.Id));
        Assert.True(await t.Redis.KeyExistsAsync($"product:{product.Id}"));
        Assert.Equal(1, metrics.Sum("reviews_total", ("action", "edit")));

        // A new rating changes the aggregate: the page cache goes.
        await (await WriteAsync(t, product.Id, account, new { rating = 3, text = "meh", version = 2 })).OkJsonAsync();
        Assert.False(await t.Redis.KeyExistsAsync($"product:{product.Id}"));
        Assert.Equal(3, (await PageAsync(t, product.Id)).GetProperty("ratingAverage").GetDouble());
    }

    [Fact]
    public async Task Request_author_name_is_ignored()
    {
        var (t, product, account) = await StartAsync("Ann L.");
        await using var host = t;

        var review = await (await WriteAsync(t, product.Id, account, new { rating = 5, authorDisplay = "Admin" })).OkJsonAsync();

        Assert.Equal("Ann L.", review.GetProperty("authorDisplay").GetString());
        await using var db = t.NewDb();
        Assert.Equal("Ann L.", (await db.Reviews.SingleAsync(r => r.ProductId == product.Id, TestContext.Current.CancellationToken)).AuthorDisplay);
    }

    [Fact]
    public async Task Review_before_name_event_uses_fallback()
    {
        var (t, product, account) = await StartAsync(name: null);
        await using var host = t;

        var review = await (await WriteAsync(t, product.Id, account, new { rating = 5 })).OkJsonAsync();

        Assert.Equal("Bloom customer", review.GetProperty("authorDisplay").GetString());
    }

    [Fact]
    public async Task Delete_own_review_then_write_again()
    {
        var (t, product, account) = await StartAsync();
        await using var host = t;
        using var metrics = new MetricCapture(t.Services);
        await (await DeleteAsync(t, product.Id, account)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await WriteAsync(t, product.Id, account, new { rating = 5 })).OkJsonAsync();
        await PageAsync(t, product.Id);

        (await DeleteAsync(t, product.Id, account)).EnsureSuccessStatusCode();

        Assert.Equal((0, 0), await RatingAsync(t, product.Id));
        Assert.False(await t.Redis.KeyExistsAsync($"product:{product.Id}"));
        Assert.Equal(1, metrics.Sum("reviews_total", ("action", "delete")));
        var again = await (await WriteAsync(t, product.Id, account, new { rating = 2 })).OkJsonAsync();
        Assert.Equal("1", again.GetProperty("version").GetString());
        Assert.Equal((2, 1), await RatingAsync(t, product.Id));
    }

    [Fact]
    public async Task C10_hiding_5_star_on_two_review_product_averaging_4_gives_3()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var product = (await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id)).Product;
        var five = await Seed.ReviewAsync(t, product.Id, 5);
        await Seed.ReviewAsync(t, product.Id, 3);
        using var metrics = new MetricCapture(t.Services);
        Assert.Equal(4, (await PageAsync(t, product.Id)).GetProperty("ratingAverage").GetDouble());

        var hidden = await (await ModerateAsync(t, "hide", five.Id, "abusive")).OkJsonAsync();

        Assert.Equal("hidden", hidden.GetProperty("status").GetString());
        Assert.Equal(Support.Id.ToString(), hidden.GetProperty("hiddenBy").GetString());
        Assert.Equal("abusive", hidden.GetProperty("hiddenReason").GetString());
        Assert.Equal("2", hidden.GetProperty("version").GetString());
        var page = await PageAsync(t, product.Id);
        Assert.Equal(3, page.GetProperty("ratingAverage").GetDouble());
        Assert.Equal(1, page.GetProperty("ratingCount").GetInt32());
        var list = await (await ListAsync(t, product.Id)).OkJsonAsync();
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(3, list.GetProperty("reviews").EnumerateArray().Single().GetProperty("rating").GetInt32());

        var shown = await (await ModerateAsync(t, "unhide", five.Id, "appeal accepted")).OkJsonAsync();

        Assert.Equal("visible", shown.GetProperty("status").GetString());
        Assert.False(shown.TryGetProperty("hiddenBy", out var by) && by.GetString() != "");
        Assert.False(shown.TryGetProperty("hiddenReason", out var why) && why.GetString() != "");
        Assert.Equal(4, (await PageAsync(t, product.Id)).GetProperty("ratingAverage").GetDouble());
        var audit = await Outbox.AuditAsync(t);
        Assert.Equal(["catalog.review-hidden", "catalog.review-unhidden"], audit.Select(a => a.Action));
        Assert.All(audit, a => { Assert.Equal("catalog", a.Service); Assert.Equal("review", a.TargetType); Assert.Equal(five.Id.ToString(), a.TargetId); Assert.Equal("support", a.ActorRole); });
        Assert.Equal("abusive", audit[0].Details.GetProperty("reason").GetString());
        Assert.Equal("appeal accepted", audit[1].Details.GetProperty("reason").GetString());
        Assert.Equal(1, metrics.Sum("reviews_total", ("action", "hide")));
        Assert.Equal(1, metrics.Sum("reviews_total", ("action", "unhide")));
    }

    [Fact]
    public async Task Hide_racing_an_edit_conflicts()
    {
        var (t, product, account) = await StartAsync();
        await using var host = t;
        var created = await (await WriteAsync(t, product.Id, account, new { rating = 4 })).OkJsonAsync();
        var version = long.Parse(created.GetProperty("version").GetString()!);
        await (await ModerateAsync(t, "hide", Guid.Parse(created.GetProperty("id").GetString()!))).OkJsonAsync();

        await (await WriteAsync(t, product.Id, account, new { rating = 1, version })).ExpectErrorAsync(409, "CONCURRENCY_CONFLICT");

        Assert.Equal((0, 0), await RatingAsync(t, product.Id));
        await AssertRecountAsync(t, product.Id);

        // Fresh version: the edit applies to the hidden review, which stays hidden and uncounted.
        var mine = (await (await ListAsync(t, product.Id, caller: TestCaller.Customer(account))).OkJsonAsync()).GetProperty("myReview");
        await (await WriteAsync(t, product.Id, account, new { rating = 1, version = long.Parse(mine.GetProperty("version").GetString()!) })).OkJsonAsync();
        Assert.Equal((0, 0), await RatingAsync(t, product.Id));
        await AssertRecountAsync(t, product.Id);
    }

    [Fact]
    public async Task Text_and_rating_validation()
    {
        var (t, product, account) = await StartAsync();
        await using var host = t;

        Assert.Equal("rating", await FieldAsync(await WriteAsync(t, product.Id, account, new { rating = 0 })));
        Assert.Equal("rating", await FieldAsync(await WriteAsync(t, product.Id, account, new { rating = 6 })));
        Assert.Equal("text", await FieldAsync(await WriteAsync(t, product.Id, account, new { rating = 5, text = new string('a', 2001) })));
        Assert.Equal((0, 0), await RatingAsync(t, product.Id));

        // 2,000 scalars of 2 UTF-16 units each are fine.
        var long2000 = string.Concat(Enumerable.Repeat("\U0001F642", 2000));
        await (await WriteAsync(t, product.Id, account, new { rating = 5, text = long2000 })).OkJsonAsync();

        var other = Guid.NewGuid();
        await Seed.EligibleAsync(t, other, product.Id);
        var blank = await (await WriteAsync(t, product.Id, other, new { rating = 5, text = "  " })).OkJsonAsync();
        Assert.True(!blank.TryGetProperty("text", out var text) || text.GetString() == "");
        await using var db = t.NewDb();
        Assert.Null((await db.Reviews.SingleAsync(r => r.AccountId == other, TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task Plans_and_hidden_products_are_not_reviewable()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var device = (await Seed.ProductAsync(t, category.Id)).Product;
        var (plan, _) = await Seed.PlanAsync(t, category.Id, [device.Id]);
        var archived = (await Seed.ProductAsync(t, category.Id, status: "archived")).Product;
        var draft = (await Seed.ProductAsync(t, category.Id, status: "draft")).Product;
        var account = Guid.NewGuid();
        foreach (var p in new[] { plan, archived, draft }) await Seed.EligibleAsync(t, account, p.Id);

        foreach (var p in new[] { plan, archived, draft })
            await (await WriteAsync(t, p.Id, account, new { rating = 5 })).ExpectErrorAsync(404, "NOT_FOUND");
        await (await WriteAsync(t, Guid.NewGuid(), account, new { rating = 5 })).ExpectErrorAsync(404, "NOT_FOUND");
        await (await ListAsync(t, plan.Id)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await ListAsync(t, archived.Id)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await ListAsync(t, draft.Id)).ExpectErrorAsync(404, "NOT_FOUND");

        // A review on a product that is no longer visible can not be edited but can be deleted.
        await Seed.ReviewAsync(t, archived.Id, 4, accountId: account);
        await (await WriteAsync(t, archived.Id, account, new { rating = 1, version = 1 })).ExpectErrorAsync(404, "NOT_FOUND");
        (await DeleteAsync(t, archived.Id, account)).EnsureSuccessStatusCode();
        Assert.Equal((0, 0), await RatingAsync(t, archived.Id));
    }

    [Fact]
    public async Task ListReviews_pages_newest_first_visible_only()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var product = (await Seed.ProductAsync(t, (await Seed.CategoryAsync(t)).Id)).Product;
        for (var i = 0; i < 25; i++)
        {
            t.Clock.Advance(TimeSpan.FromSeconds(1));
            await Seed.ReviewAsync(t, product.Id, i % 5 + 1);
            if (i % 10 == 0) await Seed.ReviewAsync(t, product.Id, 1, "hidden");
        }

        var one = await (await ListAsync(t, product.Id)).OkJsonAsync();
        var two = await (await ListAsync(t, product.Id, "?page=2")).OkJsonAsync();

        Assert.Equal(25, one.GetProperty("total").GetInt32());
        Assert.Equal(20, one.GetProperty("reviews").GetArrayLength());
        Assert.Equal(5, two.GetProperty("reviews").GetArrayLength());
        var created = one.GetProperty("reviews").EnumerateArray().Concat(two.GetProperty("reviews").EnumerateArray())
            .Select(r => DateTimeOffset.Parse(r.GetProperty("createdAt").GetString()!)).ToList();
        Assert.Equal(created.OrderByDescending(c => c), created);
        Assert.Equal(25, created.Distinct().Count());
        Assert.False(one.TryGetProperty("myReview", out _));
        Assert.Equal(0, (await (await ListAsync(t, product.Id, "?page=3")).OkJsonAsync()).GetProperty("reviews").GetArrayLength());
    }

    [Fact]
    public async Task ListReviews_returns_my_review_to_its_author()
    {
        var (t, product, account) = await StartAsync();
        await using var host = t;
        var mine = await (await WriteAsync(t, product.Id, account, new { rating = 4, text = "mine" })).OkJsonAsync();
        await (await ModerateAsync(t, "hide", Guid.Parse(mine.GetProperty("id").GetString()!))).OkJsonAsync(); // hidden reviews still come back to their author

        var own = await (await ListAsync(t, product.Id, caller: TestCaller.Customer(account))).OkJsonAsync();
        var other = await (await ListAsync(t, product.Id, caller: TestCaller.Customer(Guid.NewGuid()))).OkJsonAsync();
        var anonymous = await (await ListAsync(t, product.Id)).OkJsonAsync();
        var staff = await (await ListAsync(t, product.Id, caller: Support)).OkJsonAsync();

        Assert.Equal(0, own.GetProperty("total").GetInt32());
        Assert.Equal(0, own.GetProperty("reviews").GetArrayLength());
        Assert.Equal("2", own.GetProperty("myReview").GetProperty("version").GetString());
        Assert.Equal("mine", own.GetProperty("myReview").GetProperty("text").GetString());
        foreach (var view in new[] { other, anonymous, staff }) Assert.False(view.TryGetProperty("myReview", out _));
    }

    [Fact]
    public async Task Moderation_roles_and_admin_list()
    {
        await using var t = await TestCatalog.StartAsync(containers);
        var category = await Seed.CategoryAsync(t);
        var a = (await Seed.ProductAsync(t, category.Id)).Product;
        var b = (await Seed.ProductAsync(t, category.Id)).Product;
        var first = await Seed.ReviewAsync(t, a.Id, 5);
        var second = await Seed.ReviewAsync(t, b.Id, 2);
        var manager = TestCaller.Staff("catalog_manager");

        await (await ModerateAsync(t, "hide", first.Id, caller: manager)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        await (await ModerateAsync(t, "unhide", first.Id, caller: manager)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        await (await ModerateAsync(t, "hide", first.Id, caller: TestCaller.Customer(Guid.NewGuid()))).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        await (await t.SendAsync(HttpMethod.Get, $"{Admin}/reviews", caller: manager)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        Assert.Equal("reason", await FieldAsync(await ModerateAsync(t, "hide", first.Id, reason: " ")));
        Assert.Equal("reason", await FieldAsync(await ModerateAsync(t, "hide", first.Id, reason: new string('x', 501))));
        await (await ModerateAsync(t, "hide", Guid.NewGuid())).ExpectErrorAsync(404, "NOT_FOUND");
        Assert.Empty(await Outbox.AuditAsync(t));

        await (await ModerateAsync(t, "hide", first.Id, "spam", TestCaller.Staff("admin"))).OkJsonAsync();
        var again = await (await ModerateAsync(t, "hide", first.Id, "spam again")).OkJsonAsync(); // already hidden: success, nothing written
        Assert.Equal("2", again.GetProperty("version").GetString());
        Assert.Equal("spam", again.GetProperty("hiddenReason").GetString());
        var unhidden = await (await ModerateAsync(t, "unhide", second.Id)).OkJsonAsync(); // already visible: success, nothing written
        Assert.Equal("1", unhidden.GetProperty("version").GetString());
        Assert.Single(await Outbox.AuditAsync(t));
        Assert.Equal((0, 0), await RatingAsync(t, a.Id));
        Assert.Equal((2, 1), await RatingAsync(t, b.Id));

        var all = await (await t.SendAsync(HttpMethod.Get, $"{Admin}/reviews", caller: Support)).OkJsonAsync();
        var hidden = await (await t.SendAsync(HttpMethod.Get, $"{Admin}/reviews?status=hidden", caller: Support)).OkJsonAsync();
        var ofB = await (await t.SendAsync(HttpMethod.Get, $"{Admin}/reviews?productId={b.Id}", caller: TestCaller.Staff("admin"))).OkJsonAsync();

        Assert.Equal(2, all.GetProperty("total").GetInt32());
        var item = hidden.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(first.Id.ToString(), item.GetProperty("id").GetString());
        Assert.Equal(first.AccountId.ToString(), item.GetProperty("accountId").GetString());
        Assert.Equal("hidden", item.GetProperty("status").GetString());
        Assert.Equal("spam", item.GetProperty("hiddenReason").GetString());
        Assert.NotEqual("", item.GetProperty("hiddenBy").GetString());
        Assert.Equal(second.Id.ToString(), ofB.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal("status", await FieldAsync(await t.SendAsync(HttpMethod.Get, $"{Admin}/reviews?status=bogus", caller: Support)));
        Assert.Equal("page_size", await FieldAsync(await t.SendAsync(HttpMethod.Get, $"{Admin}/reviews?pageSize=101", caller: Support)));
    }

    [Fact]
    public async Task Customer_methods_refuse_staff()
    {
        var (t, product, _) = await StartAsync();
        await using var host = t;
        var staff = TestCaller.Staff("admin");

        await (await t.SendAsync(HttpMethod.Put, $"/api/reviews/{product.Id}", new { rating = 5 }, staff)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        await (await t.SendAsync(HttpMethod.Delete, $"/api/reviews/{product.Id}", caller: staff)).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }

    [Theory, InlineData("Mary Jane Watson", "Mary W."), InlineData("Cher", "Cher"), InlineData("  ", "Bloom customer"), InlineData(null, "Bloom customer"), InlineData("Ann  émile", "Ann é.")]
    public void First_name_last_initial(string? name, string expected) => Assert.Equal(expected, ReviewerNames.Derive(name));
}
