using System.Diagnostics;
using Bloom.Launch.Context;
using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Launch.Errors;
using Bloom.Launch.Observability;
using Bloom.Launch.Passes;
using Bloom.Launch.WaitingRoom;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;

namespace Bloom.Launch.Services;

/// <summary>01-launch §5.1 internal API (order calls it; no HTTP route, spec §6).</summary>
public sealed class InternalService(LaunchDbContext db, PassTokens tokens, LaunchMetrics metrics, IConnectionMultiplexer redis, ILogger<InternalService> logger)
    : LaunchInternal.LaunchInternalBase
{
    private record Line(Guid Sku, int Qty);

    private record ProductUse(Guid Product, long Limit, long Sum);

    /// <summary>01-launch §6.5 in its step order, in one transaction; R11 (sums in <c>long</c>, guards in <c>bigint</c>) and R12 (concurrent retries).</summary>
    public override async Task<HoldAllocationResponse> HoldAllocation(HoldAllocationRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireCustomer();
        var (orderId, lines) = Validate(request);
        var ct = context.CancellationToken;
        var clock = Stopwatch.StartNew();
        var claims = tokens.Verify(request.PassToken); // null for anything that is not a token signed with a listed key
        var launchId = claims?.LaunchId ?? Guid.Empty; // the metric label; a token that does not verify names no launch
        try
        {
            // Step 1.
            if (claims is null || claims.AccountId != caller.UserId || !await db.Launches.AnyAsync(l => l.Id == claims.LaunchId, ct))
                throw Reject(launchId, "pass_invalid", LaunchErrors.PassInvalid, "The admission pass is not valid.");
            if (claims.ExpiresAtUnixMs <= (await db.NowAsync(ct)).ToUnixTimeMilliseconds())
                throw Reject(launchId, "pass_expired", LaunchErrors.PassExpired, "The admission pass has expired.");

            for (var attempt = 0; ; attempt++)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);

                // Step 2.
                if (await ExistingAsync(orderId, caller.UserId, launchId, lines, ct) is { } existing)
                {
                    metrics.Hold(launchId, "ok");
                    return existing;
                }

                // Step 3.
                // Read inside the transaction, on every attempt: a close or release may have committed since step 1. FOR SHARE, first, before any product or SKU lock (D5, §7):
                // a room close or stage change takes the row FOR UPDATE, so it waits for this hold and a hold waits for it.
                var open = (await db.Launches.FromSql($"SELECT * FROM launches WHERE id = {launchId} FOR SHARE").AsNoTracking().ToListAsync(ct))
                    .Any(l => l.Stage == LaunchStages.PreorderOpen && l.WaitingRoomClosedAt == null);
                if (!open)
                    throw LaunchErrors.Error(LaunchErrors.LaunchNotOpen, "The launch is not open for pre-orders.");
                var skuIds = lines.Select(l => l.Sku).ToList();
                var found = await (from s in db.LaunchSkus.AsNoTracking()
                                   join p in db.LaunchProducts.AsNoTracking() on new { s.LaunchId, s.ProductId } equals new { p.LaunchId, p.ProductId }
                                   where s.LaunchId == launchId && skuIds.Contains(s.SkuId)
                                   select new { s.SkuId, s.ProductId, p.PurchaseLimit }).ToDictionaryAsync(x => x.SkuId, ct);
                if (lines.FirstOrDefault(l => !found.ContainsKey(l.Sku)) is { } missing)
                    throw LaunchErrors.Error(LaunchErrors.SkuNotInLaunch, $"SKU {missing.Sku:D} is not in this launch.");
                // R17 and the lock order (R9): products, then SKUs, each ascending.
                var products = lines.GroupBy(l => found[l.Sku].ProductId)
                    .Select(g => new ProductUse(g.Key, found[g.First().Sku].PurchaseLimit, g.Sum(l => (long)l.Qty)))
                    .OrderBy(p => p.Product.ToString("D"), StringComparer.Ordinal).ToList();

                // Step 4.
                var consumed = await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO consumed_passes (pass_id, launch_id, account_id, order_id, consumed_at) VALUES ({claims.PassId}, {launchId}, {caller.UserId}, {orderId}, now()) ON CONFLICT (pass_id) DO NOTHING", ct);
                if (consumed == 0)
                {
                    // R12: the pass is used. By this order's own concurrent first call (it has committed by now): that hold. Otherwise by another order.
                    if (await ExistingAsync(orderId, caller.UserId, launchId, lines, ct) is { } first)
                    {
                        metrics.Hold(launchId, "ok");
                        return first;
                    }
                    throw Reject(launchId, "pass_already_used", LaunchErrors.PassAlreadyUsed, "The admission pass has already been used.");
                }

                // Step 5.
                foreach (var p in products)
                {
                    if (p.Sum <= p.Limit) // R11: a sum above the limit fails without SQL
                    {
                        await db.Database.ExecuteSqlAsync(
                            $"INSERT INTO limit_usage (launch_id, account_id, product_id, held, sold) VALUES ({launchId}, {caller.UserId}, {p.Product}, 0, 0) ON CONFLICT DO NOTHING", ct);
                        var moved = await db.Database.ExecuteSqlAsync(
                            $"UPDATE limit_usage SET held = held + {p.Sum} WHERE launch_id = {launchId} AND account_id = {caller.UserId} AND product_id = {p.Product} AND held::bigint + sold + {p.Sum} <= {p.Limit}", ct);
                        if (moved == 1) continue;
                    }
                    await tx.RollbackAsync(ct);
                    var used = await db.LimitUsage.AsNoTracking().Where(u => u.LaunchId == launchId && u.AccountId == caller.UserId && u.ProductId == p.Product)
                        .Select(u => (long)u.Held + u.Sold).SingleOrDefaultAsync(ct);
                    throw Reject(launchId, "limit_exceeded", LaunchErrors.LimitExceeded, "The purchase limit would be exceeded.",
                        ("productId", p.Product.ToString("D")), ("remaining", Math.Max(0, p.Limit - used).ToString()));
                }

                // Step 6. The unique order_id makes a concurrent first call win: start again from step 2 (R12).
                var holdId = Guid.CreateVersion7();
                try
                {
                    await db.Database.ExecuteSqlAsync(
                        $"INSERT INTO holds (id, order_id, launch_id, account_id, pass_id, status, version, created_at, updated_at) VALUES ({holdId}, {orderId}, {launchId}, {caller.UserId}, {claims.PassId}, {HoldStatuses.Held}, 0, now(), now())", ct);
                    foreach (var l in lines)
                        await db.Database.ExecuteSqlAsync($"INSERT INTO hold_lines (hold_id, sku_id, product_id, quantity) VALUES ({holdId}, {l.Sku}, {found[l.Sku].ProductId}, {l.Qty})", ct);
                }
                catch (PostgresException e) when (attempt == 0 && e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "ux_holds_order_id")
                {
                    continue; // the transaction is rolled back on dispose
                }

                // Step 7. Last before COMMIT: the launch_skus rows are the hot rows at launch peak (R-02), so they stay locked for the shortest time.
                var available = new List<(Guid Sku, long Left)>();
                foreach (var l in lines)
                {
                    var left = await db.Database.SqlQuery<long>(
                        $"UPDATE launch_skus SET held = held + {l.Qty} WHERE launch_id = {launchId} AND sku_id = {l.Sku} AND held::bigint + sold + {l.Qty} <= allocation RETURNING allocation - held - sold AS \"Value\"")
                        .ToListAsync(ct);
                    if (left.Count == 0)
                    {
                        await tx.RollbackAsync(ct);
                        throw Reject(launchId, "sold_out", LaunchErrors.SoldOut, "The SKU is sold out.", ("skuId", l.Sku.ToString("D")));
                    }
                    available.Add((l.Sku, left[0]));
                }
                await tx.CommitAsync(ct);

                // Step 8, after commit.
                metrics.Hold(launchId, "ok");
                foreach (var (sku, left) in available) metrics.SetAvailable(launchId, sku, left);
                await ForgetPassAsync(launchId, caller.UserId, claims.PassId);
                return Response(holdId, lines);
            }
        }
        finally
        {
            metrics.HoldSeconds(clock.Elapsed.TotalSeconds);
        }
    }

    /// <summary>Step 2 (D6): the order's hold, when it is the same account and launch and its lines match as a set of (sku, quantity); <c>HOLD_CONFLICT</c> when anything differs.</summary>
    private async Task<HoldAllocationResponse?> ExistingAsync(Guid orderId, Guid accountId, Guid launchId, List<Line> lines, CancellationToken ct)
    {
        var hold = await db.Holds.AsNoTracking().SingleOrDefaultAsync(h => h.OrderId == orderId, ct);
        if (hold is null) return null;
        var stored = await db.HoldLines.AsNoTracking().Where(l => l.HoldId == hold.Id).OrderBy(l => l.SkuId).ToListAsync(ct);
        if (hold.AccountId != accountId || hold.LaunchId != launchId || stored.Count != lines.Count || !stored.All(s => lines.Contains(new(s.SkuId, s.Quantity))))
            throw LaunchErrors.Error(LaunchErrors.HoldConflict, "This order already holds something else.");
        return Response(hold.Id, [.. stored.Select(s => new Line(s.SkuId, s.Quantity))]);
    }

    private static HoldAllocationResponse Response(Guid holdId, List<Line> lines) =>
        new() { HoldId = holdId.ToString("D"), Lines = { lines.Select(l => new HoldLine { SkuId = l.Sku.ToString("D"), Quantity = l.Qty }) } };

    /// <summary>Best effort (§6.5 step 8, owner item 2): the hold is committed, so Redis being down only costs a warning. The pass key expires on its own.</summary>
    private async Task ForgetPassAsync(Guid launch, Guid account, Guid pass)
    {
        try
        {
            var room = redis.GetDatabase();
            await room.SortedSetRemoveAsync(RedisKeys.Passes(launch), pass.ToString("D"));
            await room.KeyDeleteAsync(RedisKeys.Pass(launch, account));
        }
        catch (Exception e)
        {
            logger.LogWarning("Could not remove the consumed pass {PassId} of launch {LaunchId} from Redis ({Error}); it expires on its own.", pass, launch, e.GetType().Name);
        }
    }

    private RpcException Reject(Guid launch, string result, string reason, string message, params (string Key, string Value)[] metadata)
    {
        metrics.Hold(launch, result);
        return LaunchErrors.Error(reason, message, metadata);
    }

    /// <summary>Spec §7 validation, before step 1. Lines come back ordered by <c>sku_id</c> as PostgreSQL orders UUIDs (R17).</summary>
    private static (Guid OrderId, List<Line> Lines) Validate(HoldAllocationRequest r)
    {
        if (!Guid.TryParseExact(r.OrderId, "D", out var orderId)) throw LaunchErrors.Validation("order_id", "order_id must be a UUID.");
        if (r.Lines.Count == 0) throw LaunchErrors.Validation("lines", "lines must not be empty.");
        var seen = new HashSet<Guid>();
        var lines = new List<Line>();
        foreach (var line in r.Lines)
        {
            if (!Guid.TryParseExact(line.SkuId, "D", out var sku)) throw LaunchErrors.Validation("lines.sku_id", "sku_id must be a UUID.");
            if (!seen.Add(sku)) throw LaunchErrors.Validation("lines.sku_id", "A sku_id appears twice.");
            if (line.Quantity < 1) throw LaunchErrors.Validation("lines.quantity", "quantity must be at least 1.");
            lines.Add(new(sku, line.Quantity));
        }
        return (orderId, [.. lines.OrderBy(l => l.Sku.ToString("D"), StringComparer.Ordinal)]);
    }
}
