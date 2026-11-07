using Bloom.Order.Api;
using Bloom.Order.Context;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Errors;
using Bloom.Shared.Concurrency;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Coupon = Bloom.Order.Data.Coupon;

namespace Bloom.Order.Services;

/// <summary>03-order §5.1, staff API. Coupons: catalog manager and admin; checkout settings: admin (plan R7). Every write audits in its transaction (spec §7).</summary>
public sealed partial class StaffService(OrderDbContext db, ConcurrentUpdates concurrent, Audit audit, TimeProvider clock) : OrderStaff.OrderStaffBase
{
    private const string CodeIndex = "ux_coupons_code_normalized";
    private const string UsesCheck = "ck_coupons_uses_le_total_limit";

    /// <summary>The writable fields of a coupon, validated.</summary>
    private sealed record CouponFields(
        string Type, int Value, string Scope, List<(string TargetType, Guid TargetId)> Items, long? MinSpendCents,
        DateTimeOffset? StartsAt, DateTimeOffset? ExpiresAt, int? TotalLimit, int? PerCustomerLimit);

    public override async Task<ListCouponsResponse> ListCoupons(ListCouponsRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.CouponEditors);
        var (skip, take) = Inputs.Paging(request.Page, request.PageSize);
        var ct = context.CancellationToken;

        var query = db.Coupons.AsNoTracking();
        if (request.HasActive) query = query.Where(c => c.Active == request.Active);
        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Skip(skip).Take(take).ToListAsync(ct);
        var ids = rows.Select(c => c.Id).ToList();
        var items = (await db.CouponScopeItems.AsNoTracking().Where(i => ids.Contains(i.CouponId)).ToListAsync(ct)).ToLookup(i => i.CouponId);
        var response = new ListCouponsResponse();
        response.Items.AddRange(rows.Select(c => Views.CouponView(c, items[c.Id])));
        return response;
    }

    public override async Task<Api.Coupon> CreateCoupon(CreateCouponRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CouponEditors);
        var (normalized, display) = Inputs.CouponCode(request.Code, "code");
        var f = Parse(request.Type, request.Value, request.Scope, request.ScopeItems, request.HasMinSpendCents ? request.MinSpendCents : null,
            request.StartsAt, request.ExpiresAt, request.HasTotalLimit ? request.TotalLimit : null, request.HasPerCustomerLimit ? request.PerCustomerLimit : null);
        var ct = context.CancellationToken;
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = new Coupon
        {
            Id = Guid.CreateVersion7(), CodeNormalized = normalized, CodeDisplay = display, Type = f.Type, Value = f.Value, Scope = f.Scope, MinSpendCents = f.MinSpendCents,
            StartsAt = f.StartsAt, ExpiresAt = f.ExpiresAt, TotalLimit = f.TotalLimit, PerCustomerLimit = f.PerCustomerLimit, Active = true, CreatedAt = now, UpdatedAt = now,
        };
        db.Coupons.Add(row);
        db.CouponScopeItems.AddRange(ScopeRows(row.Id, f));
        audit.Record(caller.UserId, caller.Role!, "order.create-coupon", "coupon", row.Id.ToString("D"), new { code = display });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: CodeIndex })
        {
            throw OrderErrors.Error(OrderErrors.CouponCodeTaken, "A coupon with that code exists.");
        }
        await tx.CommitAsync(ct);
        return await LoadAsync(row.Id, ct);
    }

    /// <summary>Every field but the code, and the scope items, in one transaction. Orders already holding a use keep their discount (OR-06).</summary>
    public override async Task<Api.Coupon> UpdateCoupon(UpdateCouponRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CouponEditors);
        var id = Inputs.Id(request.CouponId, "coupon_id");
        var f = Parse(request.Type, request.Value, request.Scope, request.ScopeItems, request.HasMinSpendCents ? request.MinSpendCents : null,
            request.StartsAt, request.ExpiresAt, request.HasTotalLimit ? request.TotalLimit : null, request.HasPerCustomerLimit ? request.PerCustomerLimit : null);
        var ct = context.CancellationToken;
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await concurrent.UpdateVersionedAsync("coupon", db.Coupons.Where(c => c.Id == id), request.Version,
                s => s.SetProperty(c => c.Type, f.Type).SetProperty(c => c.Value, f.Value).SetProperty(c => c.Scope, f.Scope).SetProperty(c => c.MinSpendCents, f.MinSpendCents)
                    .SetProperty(c => c.StartsAt, f.StartsAt).SetProperty(c => c.ExpiresAt, f.ExpiresAt).SetProperty(c => c.TotalLimit, f.TotalLimit)
                    .SetProperty(c => c.PerCustomerLimit, f.PerCustomerLimit).SetProperty(c => c.UpdatedAt, now),
                OrderErrors.NotFoundReason, ct);
        }
        catch (PostgresException e) when (e.ConstraintName == UsesCheck) // uses_count can grow at any time: the table's CHECK is the one place that decides
        {
            throw OrderErrors.Validation("total_limit", "total_limit must not be below the uses so far.");
        }
        await db.CouponScopeItems.Where(i => i.CouponId == id).ExecuteDeleteAsync(ct);
        db.CouponScopeItems.AddRange(ScopeRows(id, f));
        audit.Record(caller.UserId, caller.Role!, "order.update-coupon", "coupon", id.ToString("D"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadAsync(id, ct);
    }

    /// <summary>PRM-R13. Versioned even when the coupon is inactive already; it stays inactive. There is no way back.</summary>
    public override async Task<Api.Coupon> DeactivateCoupon(DeactivateCouponRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.CouponEditors);
        var id = Inputs.Id(request.CouponId, "coupon_id");
        var ct = context.CancellationToken;
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await concurrent.UpdateVersionedAsync("coupon", db.Coupons.Where(c => c.Id == id), request.Version,
            s => s.SetProperty(c => c.Active, false).SetProperty(c => c.UpdatedAt, now), OrderErrors.NotFoundReason, ct);
        audit.Record(caller.UserId, caller.Role!, "order.deactivate-coupon", "coupon", id.ToString("D"));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await LoadAsync(id, ct);
    }

    public override async Task<Api.CheckoutSettings> GetCheckoutSettings(GetCheckoutSettingsRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.SettingsEditors);
        return Views.SettingsView(await db.CheckoutSettings.AsNoTracking().SingleAsync(context.CancellationToken));
    }

    public override async Task<Api.CheckoutSettings> SetExpressFee(SetExpressFeeRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.SettingsEditors);
        if (request.ExpressFeeCents < 0) throw OrderErrors.Validation("express_fee_cents", "express_fee_cents must not be negative.");
        var ct = context.CancellationToken;
        var now = clock.GetUtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await concurrent.UpdateVersionedAsync("checkout settings", db.CheckoutSettings.Where(s => s.Id == 1), request.Version,
            s => s.SetProperty(x => x.ExpressFeeCents, request.ExpressFeeCents).SetProperty(x => x.UpdatedBy, caller.UserId).SetProperty(x => x.UpdatedAt, now),
            OrderErrors.NotFoundReason, ct);
        audit.Record(caller.UserId, caller.Role!, "order.set-express-fee", "checkout_settings", "1", new { expressFeeCents = request.ExpressFeeCents });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Views.SettingsView(await db.CheckoutSettings.AsNoTracking().SingleAsync(ct));
    }

    private async Task<Api.Coupon> LoadAsync(Guid id, CancellationToken ct) =>
        Views.CouponView(await db.Coupons.AsNoTracking().SingleAsync(c => c.Id == id, ct), await db.CouponScopeItems.AsNoTracking().Where(i => i.CouponId == id).ToListAsync(ct));

    private static IEnumerable<CouponScopeItem> ScopeRows(Guid couponId, CouponFields f) =>
        f.Items.Select(i => new CouponScopeItem { CouponId = couponId, TargetType = i.TargetType, TargetId = i.TargetId });

    /// <summary>The field rules of the brief: percent 1-100 or fixed above 0; <c>selected</c> needs items and <c>order</c> has none; limits at least 1; expiry after the start.</summary>
    private static CouponFields Parse(
        string type, int value, string scope, RepeatedField<ScopeItem> scopeItems, long? minSpendCents, Timestamp? startsAt, Timestamp? expiresAt, int? totalLimit, int? perCustomerLimit)
    {
        if (!CouponTypes.All.Contains(type)) throw OrderErrors.Validation("type", "type must be percent or fixed.");
        if (type == CouponTypes.Percent ? value is < 1 or > 100 : value < 1)
            throw OrderErrors.Validation("value", type == CouponTypes.Percent ? "A percent coupon is 1 to 100." : "A fixed coupon is more than 0 cents.");
        if (!CouponScopes.All.Contains(scope)) throw OrderErrors.Validation("scope", "scope must be order or selected.");
        if ((scope == CouponScopes.Selected) != (scopeItems.Count > 0))
            throw OrderErrors.Validation("scope_items", "A selected coupon needs at least one item; an order coupon has none.");
        var items = scopeItems.Select(i =>
        {
            if (!TargetTypes.All.Contains(i.TargetType)) throw OrderErrors.Validation("target_type", "target_type must be product or category.");
            return (i.TargetType, Inputs.Id(i.TargetId, "target_id"));
        }).Distinct().ToList();
        if (minSpendCents < 1) throw OrderErrors.Validation("min_spend_cents", "min_spend_cents must be more than 0.");
        var starts = startsAt?.ToDateTimeOffset();
        var expires = expiresAt?.ToDateTimeOffset();
        if (starts is { } s && expires is { } e && e <= s) throw OrderErrors.Validation("expires_at", "expires_at must be after starts_at.");
        if (totalLimit < 1) throw OrderErrors.Validation("total_limit", "total_limit must be at least 1.");
        if (perCustomerLimit < 1) throw OrderErrors.Validation("per_customer_limit", "per_customer_limit must be at least 1.");
        return new(type, value, scope, items, minSpendCents, starts, expires, totalLimit, perCustomerLimit);
    }
}
