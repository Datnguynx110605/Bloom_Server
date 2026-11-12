using Bloom.Catalog.Caching;
using Bloom.Catalog.Context;
using Bloom.Catalog.Data;
using Bloom.Catalog.Domain;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Observability;
using Bloom.Catalog.Validation;
using Bloom.Catalog.V1;
using Bloom.Shared.Concurrency;
using Bloom.Shared.Errors;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Review = Bloom.Catalog.V1.Review;
using ReviewRow = Bloom.Catalog.Data.Review;

namespace Bloom.Catalog.Services;

/// <summary>LLD 08-catalog §3 component <c>Customer API</c> (§6.5): a customer's own review. Customer actions aren't audited (plan R13).</summary>
public sealed class CustomerService(
    CatalogDbContext db, CacheInvalidation invalidation, ConcurrentUpdates concurrent, CatalogMetrics metrics, TimeProvider clock) : CatalogCustomer.CatalogCustomerBase
{
    private const string AccountIndex = "ux_reviews_product_account";

    public override async Task<Review> UpsertMyReview(UpsertMyReviewRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        if (request.Rating is < 1 or > 5) throw CatalogErrors.Validation("rating", "rating must be 1 to 5.");
        var text = Inputs.OptionalText(request.Text, "text", 2000);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.ProductId, out var productId)) throw CatalogErrors.NotFoundError("Product");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null || !Visibility.IsPublic(product)) throw CatalogErrors.NotFoundError("Product"); // a plan, a draft, an archived product (spec §4)
        var (review, action) = request.HasVersion
            ? await EditAsync(caller, productId, request.Rating, text, request.Version, ct)
            : await CreateAsync(caller, productId, request.Rating, text, ct);
        await tx.CommitAsync(ct);
        if (action is not null) metrics.Review(action);
        return ToProto(review);
    }

    public override async Task<Empty> DeleteMyReview(DeleteMyReviewRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.ProductId, out var productId)) throw CatalogErrors.NotFoundError("Review");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var review = await LockMineAsync(productId, caller.UserId, ct); // no product visibility check: a review can always be deleted
        await db.Reviews.Where(r => r.Id == review.Id).ExecuteDeleteAsync(ct);
        if (review.Status == ReviewStatuses.Visible) await ChangeRatingAsync(db, invalidation, productId, -review.Rating, -1, ct);
        await tx.CommitAsync(ct);
        metrics.Review("delete");
        return new Empty();
    }

    /// <summary>Eligibility (REV-R01), then the insert; a second review of the same account loses to the unique index (R21).</summary>
    private async Task<(ReviewRow Review, string? Action)> CreateAsync(Caller caller, Guid productId, int rating, string? text, CancellationToken ct)
    {
        if (!await db.ReviewEligibilities.AnyAsync(e => e.AccountId == caller.UserId && e.ProductId == productId, ct))
            throw CatalogErrors.Error(CatalogErrors.NotVerifiedBuyer, "Only a customer who received this product can review it.");
        var now = clock.GetUtcNow();
        var review = new ReviewRow
        {
            Id = Guid.CreateVersion7(), ProductId = productId, AccountId = caller.UserId, Rating = (short)rating, Text = text, CreatedAt = now, UpdatedAt = now,
            AuthorDisplay = await db.ReviewerNames.Where(n => n.AccountId == caller.UserId).Select(n => n.DisplayName).SingleOrDefaultAsync(ct) ?? ReviewerNames.Fallback,
        };
        db.Reviews.Add(review);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (Db.IsUniqueViolation(e, AccountIndex))
        {
            throw BloomErrors.Create(StatusCode.Aborted, BloomErrors.ConcurrencyConflict, "You have already reviewed this product.");
        }
        await ChangeRatingAsync(db, invalidation, productId, rating, 1, ct);
        return (review, "create");
    }

    /// <summary>The review row is locked first, so its status can't change under the aggregate delta; a hidden review keeps its status and isn't counted.</summary>
    private async Task<(ReviewRow Review, string? Action)> EditAsync(Caller caller, Guid productId, int rating, string? text, long version, CancellationToken ct)
    {
        var review = await LockMineAsync(productId, caller.UserId, ct);
        if (review.Version == version && review.Rating == rating && review.Text == text) return (review, null); // nothing to write (spec §4)
        var now = clock.GetUtcNow();
        review.Version = await concurrent.UpdateVersionedAsync("review", db.Reviews.Where(r => r.Id == review.Id), version,
            s => s.SetProperty(r => r.Rating, (short)rating).SetProperty(r => r.Text, text).SetProperty(r => r.UpdatedAt, now), CatalogErrors.NotFound, ct);
        var delta = rating - review.Rating;
        review.Rating = (short)rating;
        review.Text = text;
        review.UpdatedAt = now;
        if (review.Status == ReviewStatuses.Visible && delta != 0) await ChangeRatingAsync(db, invalidation, productId, delta, 0, ct);
        return (review, "edit");
    }

    private async Task<ReviewRow> LockMineAsync(Guid productId, Guid accountId, CancellationToken ct) =>
        (await db.Reviews.FromSql($"SELECT * FROM reviews WHERE product_id = {productId} AND account_id = {accountId} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
        ?? throw CatalogErrors.NotFoundError("Review");

    /// <summary>
    /// Adds to the product's rating aggregates in the caller's transaction (08-catalog §6.5). One atomic statement, so concurrent changes of
    /// different reviews add up; the product's <c>version</c> stays, an admin's edit must not conflict with a customer's rating.
    /// </summary>
    internal static async Task ChangeRatingAsync(CatalogDbContext db, CacheInvalidation invalidation, Guid productId, int sum, int count, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"UPDATE products SET rating_sum = rating_sum + {sum}, rating_count = rating_count + {count} WHERE id = {productId}", ct);
        invalidation.Product(productId);
        invalidation.Listings();
    }

    internal static Review ToProto(ReviewRow r) => new()
    {
        Id = r.Id.ToString(), ProductId = r.ProductId.ToString(), Rating = r.Rating, Text = r.Text ?? "", AuthorDisplay = r.AuthorDisplay,
        CreatedAt = Timestamp.FromDateTimeOffset(r.CreatedAt), UpdatedAt = Timestamp.FromDateTimeOffset(r.UpdatedAt), Version = r.Version,
    };
}
