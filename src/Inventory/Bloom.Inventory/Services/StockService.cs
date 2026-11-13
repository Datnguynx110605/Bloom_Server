using System.Diagnostics;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Inventory.Errors;
using Bloom.Inventory.Observability;
using Bloom.Shared.Persistence;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Services;

/// <summary>02-inventory §3 component <c>Stock API</c> (§5.1): <c>order</c> and <c>fulfillment</c> call it over plain gRPC; it has no HTTP route (spec §6). Each call runs its own transaction (plan R10).</summary>
public sealed class StockService(InventoryDbContext db, StockLocks locks, Ledger ledger, InventoryMetrics metrics) : InventoryStock.InventoryStockBase
{
    /// <summary>§6.1 plus plan R21: <c>regular</c> / <c>reserved</c> allocations, ledger <c>reserved</c>. Records <c>reserve_total</c> / <c>reserve_seconds</c> for the two outcomes the metric knows.</summary>
    public override async Task<Empty> ReserveStock(ReserveStockRequest request, ServerCallContext context)
    {
        var orderId = Inputs.Id(request.OrderId, "order_id");
        var lines = ParseLines(request.Lines);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await CommitAsync(orderId, orderId, lines, AllocationKinds.Regular, AllocationStatuses.Reserved, context.CancellationToken);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.FailedPrecondition && e.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason != InventoryErrors.OrderReleased) // SOLD_OUT or SKU_NOT_STOCKED; ORDER_RELEASED is no outcome the metric knows
        {
            metrics.Reserve("sold_out", Stopwatch.GetElapsedTime(started).TotalSeconds);
            throw;
        }
        metrics.Reserve("ok", Stopwatch.GetElapsedTime(started).TotalSeconds);
        return new Empty();
    }

    /// <summary>Plan R20: one item per distinct id in request order; a SKU never received has none to sell.</summary>
    public override async Task<GetAvailabilityResponse> GetAvailability(GetAvailabilityRequest request, ServerCallContext context)
    {
        var ids = request.SkuIds.Select(s => (Text: s, Id: Inputs.Id(s, "sku_ids"))).DistinctBy(x => x.Id).ToList();
        var guids = ids.Select(x => x.Id).ToList();
        var available = await db.StockItems.AsNoTracking().Where(i => guids.Contains(i.SkuId))
            .ToDictionaryAsync(i => i.SkuId, i => i.OnHand - i.Reserved, context.CancellationToken);
        var response = new GetAvailabilityResponse();
        response.Items.AddRange(ids.Select(x => new SkuAvailability { SkuId = x.Text, Available = available.GetValueOrDefault(x.Id) }));
        return response;
    }

    /// <summary>§6.9 plus plan R21: <c>reship</c> / <c>committed</c> allocations, ledger <c>committed</c>.</summary>
    public override async Task<Empty> CommitForReship(CommitForReshipRequest request, ServerCallContext context)
    {
        var reshipId = Inputs.Id(request.ReshipId, "reship_id");
        var orderId = Inputs.Id(request.OrderId, "order_id");
        await CommitAsync(reshipId, orderId, ParseLines(request.Lines), AllocationKinds.Reship, AllocationStatuses.Committed, context.CancellationToken);
        return new Empty();
    }

    /// <summary>Plan R15: non-empty, every SKU a UUID, quantity above 0, no SKU twice.</summary>
    private static List<(Guid SkuId, int Quantity)> ParseLines(IEnumerable<StockLine> lines)
    {
        var parsed = lines.Select(l => (SkuId: Inputs.Id(l.SkuId, "sku_id"), l.Quantity)).ToList();
        if (parsed.Count == 0) throw InventoryErrors.Validation("lines", "At least one line is required.");
        if (parsed.Any(l => l.Quantity <= 0)) throw InventoryErrors.Validation("quantity", "The quantity must be positive.");
        if (parsed.DistinctBy(l => l.SkuId).Count() != parsed.Count) throw InventoryErrors.Validation("lines", "A SKU appears on more than one line.");
        return parsed;
    }

    /// <summary>
    /// Plan R9, R21. A regular reserve first takes the order's lock and checks the tombstone (IR-05); then compares with the allocations already held under <paramref name="refId"/>, then runs the guarded updates one SKU at a time in
    /// ascending <c>sku_id</c> (the update takes the row lock, so every writer locks in the same order), then inserts the allocations and ledger rows.
    /// A concurrent duplicate loses on the allocation key, or on a shortage the winner caused; it rolls back and compares once more.
    /// </summary>
    private async Task CommitAsync(Guid refId, Guid orderId, List<(Guid SkuId, int Quantity)> lines, string kind, string status, CancellationToken ct)
    {
        var sorted = lines.OrderBy(l => l.SkuId).ToList(); // Guid orders like Postgres' uuid (the text form's field order)
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (kind == AllocationKinds.Regular)
            {
                await locks.LockOrderAsync(orderId, ct); // IR-05: a release in flight has committed by now
                // LLD-ADR-014: a released order never holds stock again. Before the held comparison: a replay after a release must not answer ok.
                if (await db.ReleasedOrders.AnyAsync(r => r.OrderId == orderId, ct))
                    throw InventoryErrors.Error(InventoryErrors.OrderReleased, "The order was released.");
            }
            var held = await db.StockAllocations.AsNoTracking().Where(a => a.RefId == refId).Select(a => new { a.SkuId, a.Quantity }).ToListAsync(ct);
            if (held.Count > 0)
            {
                if (held.Count == sorted.Count && held.All(h => sorted.Contains((h.SkuId, h.Quantity)))) return;
                throw InventoryErrors.Error(InventoryErrors.ReservationConflict, "This id was already used with different lines.");
            }

            var now = await db.Database.NowAsync(ct);
            RpcException? shortage = null;
            foreach (var (skuId, q) in sorted)
            {
                var changed = kind == AllocationKinds.Reship
                    ? await db.Database.ExecuteSqlAsync($"""
                        UPDATE stock_items SET on_hand = on_hand - {q}, committed = committed + {q}, updated_at = {now}
                        WHERE sku_id = {skuId} AND on_hand - reserved >= {q}
                        """, ct)
                    : await db.Database.ExecuteSqlAsync($"""
                        UPDATE stock_items SET reserved = reserved + {q}, updated_at = {now}
                        WHERE sku_id = {skuId} AND on_hand - reserved >= {q}
                        """, ct);
                if (changed > 0) continue;
                shortage = await db.StockItems.AnyAsync(i => i.SkuId == skuId, ct)
                    ? InventoryErrors.SoldOut(skuId)
                    : InventoryErrors.Error(InventoryErrors.SkuNotStocked, "The SKU has never been received.");
                break;
            }
            if (shortage is not null)
            {
                // A concurrent duplicate that took the last units makes this update match nothing: roll back and compare once more, so a replay is ok, not sold out.
                if (attempt == 0) continue;
                throw shortage;
            }

            foreach (var (skuId, q) in sorted)
            {
                db.StockAllocations.Add(new StockAllocation
                {
                    RefId = refId, SkuId = skuId, OrderId = orderId, Kind = kind, Quantity = q, Status = status, CreatedAt = now, UpdatedAt = now,
                });
                if (kind == AllocationKinds.Reship) ledger.Add(skuId, EntryTypes.Committed, new(OnHand: -q, Committed: q), RefTypes.Reship, refId, now);
                else ledger.Add(skuId, EntryTypes.Reserved, new(Reserved: q), RefTypes.Order, refId, now);
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException e) when (Db.IsUniqueViolation(e, "PK_stock_allocations"))
            {
                db.ChangeTracker.Clear();
                continue; // a duplicate committed first, also after a shortage retry (IN-3): the tx is disposed (rolled back) here and the retry compares with the winner's rows
            }
            await tx.CommitAsync(ct);
            return;
        }
    }
}
