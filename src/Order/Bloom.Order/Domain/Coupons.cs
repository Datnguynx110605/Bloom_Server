using Bloom.Order.Data;
using Bloom.Order.Errors;
using Bloom.Order.Observability;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Domain;

/// <summary>Coupon counters and uses (03-order §6.8, plan R13). Writes run in the caller's transaction.</summary>
public sealed class Coupons(OrderDbContext db, OrderMetrics metrics)
{
    private const string Savepoint = "coupon_hold";

    internal sealed class UseRow
    {
        public Guid CouponId { get; set; }
        public Guid AccountId { get; set; }
    }

    /// <summary>The checkout pre-check: the coupon exists, is active and in its dates, and has uses left, in total and for the customer. Reads only, nothing is counted.</summary>
    public async Task<(Guid Id, CouponTerms Terms)> CheckAsync(string codeNormalized, Guid accountId, CancellationToken ct)
    {
        var coupon = await db.Coupons.AsNoTracking().SingleOrDefaultAsync(c => c.CodeNormalized == codeNormalized, ct)
            ?? throw OrderErrors.Error(OrderErrors.CouponNotFound, "There is no coupon with that code.");
        if (Refusal(coupon, await db.Database.NowAsync(ct)) is { } reason) throw OrderErrors.Error(reason, "The coupon cannot be used.");
        if (coupon.TotalLimit <= coupon.UsesCount) throw LimitReached();
        if (coupon.PerCustomerLimit is { } limit
            && await db.CouponCustomerUsages.AsNoTracking().Where(u => u.CouponId == coupon.Id && u.AccountId == accountId).Select(u => u.UsesCount).SingleOrDefaultAsync(ct) >= limit)
            throw LimitReached();

        var items = await db.CouponScopeItems.AsNoTracking().Where(i => i.CouponId == coupon.Id).ToListAsync(ct);
        return (coupon.Id, new CouponTerms(coupon.Type, coupon.Value, coupon.Scope, [.. items.Select(i => (i.TargetType, i.TargetId))], coupon.MinSpendCents));
    }

    /// <summary>
    /// §6.8 in the open transaction: the guarded total counter, then the customer's usage with its guarded counter, then the use as <c>held</c>. Throws the validity reason or
    /// <c>COUPON_LIMIT_REACHED</c>, with every count of this call undone (a savepoint) and the transaction still usable.
    /// </summary>
    public async Task HoldAsync(Guid couponId, Guid accountId, Guid orderId, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("The coupon hold needs an open transaction.");
        await tx.CreateSavepointAsync(Savepoint, ct);

        var counted = await db.Database.ExecuteSqlAsync($"""
            UPDATE coupons SET uses_count = uses_count + 1
            WHERE id = {couponId} AND active AND (total_limit IS NULL OR uses_count + 1 <= total_limit)
              AND (starts_at IS NULL OR starts_at <= now()) AND (expires_at IS NULL OR expires_at > now())
            """, ct);
        string? refusal = null;
        if (counted == 0)
        {
            var coupon = await db.Coupons.AsNoTracking().SingleOrDefaultAsync(c => c.Id == couponId, ct);
            refusal = coupon is null ? OrderErrors.CouponNotFound : Refusal(coupon, await db.Database.NowAsync(ct)) ?? OrderErrors.CouponLimitReached;
        }
        else
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO coupon_customer_usage (coupon_id, account_id, uses_count) VALUES ({couponId}, {accountId}, 0) ON CONFLICT DO NOTHING", ct);
            var perCustomer = await db.Database.ExecuteSqlAsync($"""
                UPDATE coupon_customer_usage u SET uses_count = u.uses_count + 1 FROM coupons c
                WHERE u.coupon_id = {couponId} AND u.account_id = {accountId} AND c.id = u.coupon_id
                  AND (c.per_customer_limit IS NULL OR u.uses_count + 1 <= c.per_customer_limit)
                """, ct);
            if (perCustomer == 0) refusal = OrderErrors.CouponLimitReached;
        }

        if (refusal is not null)
        {
            await tx.RollbackToSavepointAsync(Savepoint, ct);
            if (refusal == OrderErrors.CouponLimitReached) metrics.Coupon("limit_reached");
            throw OrderErrors.Error(refusal, "The coupon cannot be used.");
        }
        await tx.ReleaseSavepointAsync(Savepoint, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO coupon_uses (order_id, coupon_id, account_id, status, created_at, updated_at)
            VALUES ({orderId}, {couponId}, {accountId}, {CouponUseStatuses.Held}, now(), now())
            """, ct);
        metrics.Coupon("held");
    }

    /// <summary>
    /// A guarded move of the order's use (<c>held → consumed</c>, <c>held → released</c>, <c>consumed → restored</c>). Release and restore subtract 1 from both counters,
    /// guarded by <c>uses_count &gt; 0</c>. False when the use is not in <paramref name="from"/> (no coupon, or already moved).
    /// </summary>
    public async Task<bool> MoveUseAsync(Guid orderId, string from, string to, CancellationToken ct)
    {
        var use = (await db.Database.SqlQuery<UseRow>($"""
            UPDATE coupon_uses SET status = {to}, updated_at = now() WHERE order_id = {orderId} AND status = {from}
            RETURNING coupon_id AS "CouponId", account_id AS "AccountId"
            """).ToListAsync(ct)).SingleOrDefault();
        if (use is null) return false;

        if (to is CouponUseStatuses.Released or CouponUseStatuses.Restored)
        {
            await db.Database.ExecuteSqlAsync($"UPDATE coupons SET uses_count = uses_count - 1 WHERE id = {use.CouponId} AND uses_count > 0", ct);
            await db.Database.ExecuteSqlAsync($"UPDATE coupon_customer_usage SET uses_count = uses_count - 1 WHERE coupon_id = {use.CouponId} AND account_id = {use.AccountId} AND uses_count > 0", ct);
        }
        metrics.Coupon(to);
        return true;
    }

    /// <summary>The reason the coupon is out of use now (inactive, not started, expired), or null.</summary>
    private static string? Refusal(Coupon c, DateTimeOffset now) =>
        !c.Active ? OrderErrors.CouponInactive
        : c.StartsAt > now ? OrderErrors.CouponNotStarted
        : c.ExpiresAt <= now ? OrderErrors.CouponExpired
        : null;

    private static Grpc.Core.RpcException LimitReached() => OrderErrors.Error(OrderErrors.CouponLimitReached, "The coupon has no uses left.");
}
