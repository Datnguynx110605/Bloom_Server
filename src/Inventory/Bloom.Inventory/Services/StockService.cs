using System.Diagnostics;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Inventory.Errors;
using Bloom.Inventory.Observability;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Services;

/// <summary>02-inventory §3 component <c>Stock API</c> (§5.1): <c>order</c> and <c>fulfillment</c> call it over plain gRPC; it has no HTTP route (spec §6). Each call runs its own transaction (plan R10).</summary>
public sealed class StockService(InventoryDbContext db, Ledger ledger, InventoryMetrics metrics, TimeProvider clock) : InventoryStock.InventoryStockBase
{
    /// <summary>§6.1 plus plan R21: <c>regular</c> / <c>reserved</c> allocations, ledger <c>reserved</c>. Records <c>reserve_total</c> / <c>reserve_seconds</c> for the two outcomes the metric knows.</summary>
    public override async Task<Empty> ReserveStock(ReserveStockRequest request, ServerCallContext context)
    {
        var orderId = ParseId(request.OrderId, "order_id");
        var lines = ParseLines(request.Lines);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await CommitAsync(orderId, orderId, lines, AllocationKinds.Regular, AllocationStatuses.Reserved, context.CancellationToken);
        }
        catch (RpcException e) when (IsStockShortage(e))
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
        var ids = request.SkuIds.Select(s => (Text: s, Id: ParseId(s, "sku_ids"))).DistinctBy(x => x.Id).ToList();
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
        var reshipId = ParseId(request.ReshipId, "reship_id");
        var orderId = ParseId(request.OrderId, "order_id");
        await CommitAsync(reshipId, orderId, ParseLines(request.Lines), AllocationKinds.Reship, AllocationStatuses.Committed, context.CancellationToken);
        return new Empty();
    }

    private static bool IsStockShortage(RpcException e) =>
        e.GetRpcStatus()?.Details.Select(d => d.Is(Google.Rpc.ErrorInfo.Descriptor) ? d.Unpack<Google.Rpc.ErrorInfo>().Reason : null)
            .Any(r => r is InventoryErrors.SoldOutReason or InventoryErrors.SkuNotStocked) == true;

    private static Guid ParseId(string value, string field) =>
        Guid.TryParseExact(value, "D", out var id) ? id : throw InventoryErrors.Validation(field, $"{field} must be a UUID.");

    /// <summary>Plan R15: non-empty, every SKU a UUID, quantity above 0, no SKU twice.</summary>
    private static List<(Guid SkuId, int Quantity)> ParseLines(IEnumerable<StockLine> lines)
    {
        var parsed = lines.Select(l => (SkuId: ParseId(l.SkuId, "sku_id"), l.Quantity)).ToList();
        if (parsed.Count == 0) throw InventoryErrors.Validation("lines", "At least one line is required.");
        if (parsed.Any(l => l.Quantity <= 0)) throw InventoryErrors.Validation("quantity", "The quantity must be positive.");
        if (parsed.DistinctBy(l => l.SkuId).Count() != parsed.Count) throw InventoryErrors.Validation("lines", "A SKU appears on more than one line.");
        return parsed;
    }

    /// <summary>
    /// Plan R9, R21. Compares with the allocations already held under <paramref name="refId"/>, then runs the guarded updates one SKU at a time in
    /// ascending <c>sku_id</c> (the update takes the row lock, so every writer locks in the same order), then inserts the allocations and ledger rows.
    /// A concurrent duplicate loses on the allocation key, or on a shortage the winner caused; it rolls back and compares once more.
    /// </summary>
    private async Task CommitAsync(Guid refId, Guid orderId, List<(Guid SkuId, int Quantity)> lines, string kind, string status, CancellationToken ct)
    {
        var sorted = lines.OrderBy(l => l.SkuId).ToList(); // Guid orders like Postgres' uuid (the text form's field order)
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var held = await db.StockAllocations.AsNoTracking().Where(a => a.RefId == refId).Select(a => new { a.SkuId, a.Quantity }).ToListAsync(ct);
            if (held.Count > 0)
            {
                if (held.Count == sorted.Count && held.All(h => sorted.Contains((h.SkuId, h.Quantity)))) return;
                throw InventoryErrors.Error(InventoryErrors.ReservationConflict, "This id was already used with different lines.");
            }

            var now = clock.GetUtcNow();
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
                if (kind == AllocationKinds.Reship) ledger.Add(skuId, EntryTypes.Committed, new(OnHand: -q, Committed: q), RefTypes.Reship, refId);
                else ledger.Add(skuId, EntryTypes.Reserved, new(Reserved: q), RefTypes.Order, refId);
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException e) when (attempt == 0 && Db.IsUniqueViolation(e, "PK_stock_allocations"))
            {
                db.ChangeTracker.Clear();
                continue; // the tx is disposed (rolled back) here; the retry compares with the winner's rows
            }
            await tx.CommitAsync(ct);
            return;
        }
    }
}
