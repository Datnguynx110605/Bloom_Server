using Bloom.Inventory.Context;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Inventory.Errors;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Services;

/// <summary>02-inventory §3 component <c>Packing API</c> (§5.1, §6.6): fulfillment assigns serials to a committed allocation while packing. Plan R16; each call runs its own transaction (R10) and writes no ledger rows.</summary>
public sealed class PackingService(InventoryDbContext db, StockLocks locks, Audit audit, TimeProvider clock) : InventoryPacking.InventoryPackingBase
{
    public override async Task<Empty> AssignSerial(AssignSerialRequest request, ServerCallContext context)
    {
        var refId = Inputs.Id(request.RefId, "ref_id");
        var skuId = Inputs.Id(request.SkuId, "sku_id");
        var serial = Inputs.Serial(request.Serial, "serial");
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var allocation = await LockAsync(skuId, refId, ct);
        var unit = await FindAsync(serial, ct);
        if (unit is { Status: SerialStatuses.Assigned } && unit.RefId == refId && unit.SkuId == skuId) return new Empty(); // retry
        if (allocation is not { Status: AllocationStatuses.Committed })
            throw InventoryErrors.Error(InventoryErrors.NotCommitted, "The allocation is not committed.");
        var assigned = await db.SerialUnits.CountAsync(s => s.RefId == refId && s.SkuId == skuId && s.Status == SerialStatuses.Assigned, ct);
        if (assigned >= allocation.Quantity)
            throw InventoryErrors.Error(InventoryErrors.SerialCountExceeded, "The allocation already has all its serials.");

        await SetAsync(serial, skuId, SerialStatuses.InStock, SerialStatuses.Assigned, refId, ct);
        Record(context, "inventory.serial-assigned", serial, new { refId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new Empty();
    }

    public override async Task<Empty> UnassignSerial(UnassignSerialRequest request, ServerCallContext context)
    {
        var refId = Inputs.Id(request.RefId, "ref_id");
        var serial = Inputs.Serial(request.Serial, "serial");
        var ct = context.CancellationToken;
        // No sku_id in the request: read the serial's SKU, lock, then look again under the lock (plan R9).
        var skuId = (await FindAsync(serial, ct))?.SkuId ?? throw NotAvailable();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await LockAsync(skuId, refId, ct);
        var unit = await FindAsync(serial, ct) ?? throw NotAvailable();
        if (unit.Status == SerialStatuses.InStock) return new Empty(); // retry
        if (unit.Status != SerialStatuses.Assigned || unit.RefId != refId) throw NotAvailable();

        await SetAsync(serial, skuId, SerialStatuses.Assigned, SerialStatuses.InStock, null, ct);
        Record(context, "inventory.serial-unassigned", serial, new { refId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new Empty();
    }

    public override async Task<Empty> ReplaceSerial(ReplaceSerialRequest request, ServerCallContext context)
    {
        var refId = Inputs.Id(request.RefId, "ref_id");
        var oldSerial = Inputs.Serial(request.OldSerial, "old_serial");
        var newSerial = Inputs.Serial(request.NewSerial, "new_serial");
        var ct = context.CancellationToken;
        var skuId = (await FindAsync(oldSerial, ct))?.SkuId ?? throw NotAvailable();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await LockAsync(skuId, refId, ct);
        var old = await FindAsync(oldSerial, ct) ?? throw NotAvailable();
        var next = await FindAsync(newSerial, ct);
        if (old.Status == SerialStatuses.InStock && next is { Status: SerialStatuses.Assigned } && next.RefId == refId && next.SkuId == skuId) return new Empty(); // retry
        if (old.Status != SerialStatuses.Assigned || old.RefId != refId) throw NotAvailable();
        if (next is not { Status: SerialStatuses.InStock } || next.SkuId != skuId) throw NotAvailable();

        await SetAsync(oldSerial, skuId, SerialStatuses.Assigned, SerialStatuses.InStock, null, ct);
        await SetAsync(newSerial, skuId, SerialStatuses.InStock, SerialStatuses.Assigned, refId, ct);
        Record(context, "inventory.serial-replaced", newSerial, new { refId, oldSerial });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new Empty();
    }

    /// <summary>Plan R9: the SKU's <c>stock_items</c> lock first, then the allocation row <c>(ref_id, sku_id)</c> <c>FOR UPDATE</c>. Null when there is no allocation.</summary>
    private async Task<StockAllocation?> LockAsync(Guid skuId, Guid refId, CancellationToken ct)
    {
        await locks.LockAsync([skuId], ct);
        return (await db.StockAllocations.FromSql($"SELECT * FROM stock_allocations WHERE ref_id = {refId} AND sku_id = {skuId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
    }

    private Task<SerialUnit?> FindAsync(string serial, CancellationToken ct) => db.SerialUnits.AsNoTracking().SingleOrDefaultAsync(s => s.Serial == serial, ct);

    /// <summary>The guarded update of plan R16; 0 rows (another SKU, another status) is <c>SERIAL_NOT_AVAILABLE</c>.</summary>
    private async Task SetAsync(string serial, Guid skuId, string from, string to, Guid? refId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var changed = await db.Database.ExecuteSqlAsync($"""
            UPDATE serial_units SET status = {to}, ref_id = {refId}, version = version + 1, updated_at = {now}
            WHERE serial = {serial} AND sku_id = {skuId} AND status = {from}
            """, ct);
        if (changed == 0) throw NotAvailable();
    }

    /// <summary>Owner item 2: a call without the gateway's headers audits with an empty actor.</summary>
    private void Record(ServerCallContext context, string action, string serial, object details)
    {
        var caller = Caller.TryFrom(context);
        audit.Record(caller?.UserId ?? Guid.Empty, caller?.Role ?? "", action, "serial", serial, details);
    }

    private static RpcException NotAvailable() => InventoryErrors.Error(InventoryErrors.SerialNotAvailable, "The serial is not available for this allocation.");
}
