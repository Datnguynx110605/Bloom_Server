using Bloom.Catalog.Context;
using Bloom.Catalog.Data;
using Bloom.Catalog.Errors;
using Bloom.Catalog.Validation;
using Bloom.Catalog.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using ReviewRow = Bloom.Catalog.Data.Review;

namespace Bloom.Catalog.Services;

/// <summary>Review moderation (08-catalog §6.5): support and admin (plan R7).</summary>
public sealed partial class AdminService
{
    public override async Task<AdminListReviewsResponse> AdminListReviews(AdminListReviewsRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.ModerationRoles);
        var (_, size, skip) = Inputs.Paging(request.Page, request.PageSize, 50, 100);
        if (request.Status != "" && !ReviewStatuses.All.Contains(request.Status))
            throw CatalogErrors.Validation("status", "status must be visible or hidden.");
        Guid? productId = null;
        if (request.ProductId != "")
            productId = Guid.TryParse(request.ProductId, out var parsed) ? parsed : throw CatalogErrors.Validation("product_id", "product_id must be a UUID.");
        var ct = context.CancellationToken;

        var query = db.Reviews.AsNoTracking();
        if (productId is not null) query = query.Where(r => r.ProductId == productId);
        if (request.Status != "") query = query.Where(r => r.Status == request.Status);
        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip(skip).Take(size).ToListAsync(ct);
        var response = new AdminListReviewsResponse { Total = await query.CountAsync(ct) };
        response.Items.AddRange(rows.Select(ToAdminProto));
        return response;
    }

    public override Task<AdminReview> HideReview(ModerateReviewRequest request, ServerCallContext context) => ModerateAsync(request, context, hide: true);

    public override Task<AdminReview> UnhideReview(ModerateReviewRequest request, ServerCallContext context) => ModerateAsync(request, context, hide: false);

    /// <summary>
    /// Hide or unhide, bumping the review version so a stale customer edit conflicts (R21). The row is locked first, so a racing edit sees
    /// the new version and the aggregate delta is computed from the status this call saw.
    /// </summary>
    private async Task<AdminReview> ModerateAsync(ModerateReviewRequest request, ServerCallContext context, bool hide)
    {
        var caller = Caller.From(context).RequireStaff(Caller.ModerationRoles);
        var reason = Inputs.Name(request.Reason, "reason", 500);
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.Id, out var id)) throw CatalogErrors.NotFoundError("Review");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var review = (await db.Reviews.FromSql($"SELECT * FROM reviews WHERE id = {id} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
            ?? throw CatalogErrors.NotFoundError("Review");
        var status = hide ? ReviewStatuses.Hidden : ReviewStatuses.Visible;
        if (review.Status == status) return ToAdminProto(review); // nothing to change: no write, no audit (R13)

        var hiddenBy = hide ? caller.UserId : (Guid?)null;
        var hiddenReason = hide ? reason : null;
        review.Version = await concurrent.UpdateVersionedAsync("review", db.Reviews.Where(r => r.Id == id), review.Version,
            s => s.SetProperty(r => r.Status, status).SetProperty(r => r.HiddenBy, hiddenBy).SetProperty(r => r.HiddenReason, hiddenReason), CatalogErrors.NotFound, ct);
        review.Status = status;
        review.HiddenBy = hiddenBy;
        review.HiddenReason = hiddenReason;
        await CustomerService.ChangeRatingAsync(db, invalidation, review.ProductId, hide ? -review.Rating : review.Rating, hide ? -1 : 1, ct);
        audit.Record(caller, hide ? "catalog.review-hidden" : "catalog.review-unhidden", "review", id, new { reason });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.Review(hide ? "hide" : "unhide");
        return ToAdminProto(review);
    }

    private static AdminReview ToAdminProto(ReviewRow r) => new()
    {
        Id = r.Id.ToString(), ProductId = r.ProductId.ToString(), Rating = r.Rating, Text = r.Text ?? "", AuthorDisplay = r.AuthorDisplay,
        CreatedAt = Timestamp.FromDateTimeOffset(r.CreatedAt), UpdatedAt = Timestamp.FromDateTimeOffset(r.UpdatedAt), Version = r.Version,
        AccountId = r.AccountId?.ToString() ?? "", Status = r.Status, HiddenBy = r.HiddenBy?.ToString() ?? "", HiddenReason = r.HiddenReason ?? "",
    };
}
