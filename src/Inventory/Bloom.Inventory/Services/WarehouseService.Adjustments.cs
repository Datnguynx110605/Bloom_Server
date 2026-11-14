using Bloom.Inventory.Context;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Inventory.Errors;
using Bloom.Inventory.V1;
using Bloom.Shared.Persistence;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Inventory.Services;

public sealed partial class WarehouseService
{
    private static readonly string[] DecreaseReasons = [ReasonCodes.DamagedInWarehouse, ReasonCodes.LostInWarehouse, ReasonCodes.CountCorrection, ReasonCodes.Other];
    private static readonly string[] IncreaseReasons = [ReasonCodes.Found, ReasonCodes.CountCorrection, ReasonCodes.Other];

    /// <summary>A request that passed the checks that need no database state. Serial and IMEI fields are normalized; null when not given.</summary>
    private sealed record Adjustment(Guid SkuId, int Delta, string Reason, string Note, string? Serial, string? NewSerial, string? NewImei);

    /// <summary>02-inventory §6.8 and spec §4: role, request-only checks (R15), then everything that reads state inside the idempotency guard, so a retry replays the first outcome.</summary>
    public override async Task<AdjustStockResponse> AdjustStock(AdjustStockRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.StockWriters);
        var ct = context.CancellationToken;
        var a = ValidateAdjustment(request);

        return await guard.ExecuteAsync(request, context, async _ =>
        {
            var now = await db.Database.NowAsync(ct);
            if (!(await locks.LockAsync([a.SkuId], ct)).TryGetValue(a.SkuId, out var item))
                throw InventoryErrors.Error(InventoryErrors.SkuNotStocked, "The SKU has no stock record yet.");
            CheckSerialShape(a, item.Serialized);

            var n = Math.Abs(a.Delta);
            var damaged = a.Delta < 0 && a.Reason == ReasonCodes.DamagedInWarehouse ? n : 0;
            var lost = a.Delta < 0 && a.Reason == ReasonCodes.LostInWarehouse ? n : 0;
            var adjustmentId = Guid.CreateVersion7();
            ledger.Actor = caller.UserId.ToString("D");

            if (a.Delta < 0 && a.Serial is not null) // serial first: it is the check Warehouse can fix, and the update waits for a racing AssignSerial
            {
                var status = lost > 0 ? SerialStatuses.Lost : SerialStatuses.Damaged;
                if (await db.Database.ExecuteSqlAsync($"""
                    UPDATE serial_units SET status = {status}, version = version + 1, updated_at = {now}
                    WHERE serial = {a.Serial} AND sku_id = {a.SkuId} AND status = {SerialStatuses.InStock}
                    """, ct) == 0)
                {
                    var unit = await db.SerialUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Serial == a.Serial, ct);
                    throw unit is { Status: SerialStatuses.Assigned } && unit.SkuId == a.SkuId
                        ? InventoryErrors.Error(InventoryErrors.SerialAssigned, "The serial is assigned to an order.")
                        : InventoryErrors.Error(InventoryErrors.SerialNotAvailable, "The serial is not in stock for this SKU.");
                }
            }
            else if (a.Serial is not null) // increase: a lost unit comes back
            {
                if (await db.Database.ExecuteSqlAsync($"""
                    UPDATE serial_units SET status = {SerialStatuses.InStock}, version = version + 1, updated_at = {now}
                    WHERE serial = {a.Serial} AND sku_id = {a.SkuId} AND status = {SerialStatuses.Lost}
                    """, ct) == 0)
                    throw InventoryErrors.Error(InventoryErrors.SerialNotAvailable, "The serial is not a lost unit of this SKU.");
            }
            else if (a.NewSerial is not null)
            {
                if (await db.SerialUnits.AnyAsync(u => u.Serial == a.NewSerial, ct)) throw InventoryErrors.Validation("new_serials", "The serial already exists.");
                if (a.NewImei is not null && await db.SerialUnits.AnyAsync(u => u.Imei == a.NewImei, ct)) throw InventoryErrors.Validation("new_imeis", "The IMEI already exists.");
                db.SerialUnits.Add(new SerialUnit
                {
                    Serial = a.NewSerial, Imei = a.NewImei, SkuId = a.SkuId, Status = SerialStatuses.InStock, ReceiptId = adjustmentId, UpdatedAt = now,
                });
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "serial_units" })
                {
                    throw InventoryErrors.Validation("new_serials", "The serial or IMEI was added concurrently.");
                }
            }

            var lostBack = a.Delta > 0 && a.Serial is not null ? 1 : 0;
            var need = Math.Max(-a.Delta, 0); // a decrease can't touch units promised to an order (IR-06); an increase needs nothing but a lost unit to bring back
            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE stock_items SET on_hand = on_hand + {a.Delta}, damaged = damaged + {damaged}, lost = lost + {lost - lostBack}, updated_at = {now}
                WHERE sku_id = {a.SkuId} AND on_hand - reserved >= {need} AND lost + {lost - lostBack} >= 0
                """, ct) == 0)
                throw a.Delta < 0
                    ? InventoryErrors.Error(InventoryErrors.AdjustmentExceedsAvailable, "Fewer units are available than the adjustment removes.")
                    : new InvalidOperationException($"SKU {a.SkuId} has a lost serial but lost = 0.");

            var serial = a.Serial ?? a.NewSerial;
            db.StockAdjustments.Add(new StockAdjustment
            {
                Id = adjustmentId, SkuId = a.SkuId, Serial = serial, Delta = a.Delta, ReasonCode = a.Reason, Note = a.Note, StaffId = caller.UserId, CreatedAt = now,
            });
            ledger.Add(a.SkuId, EntryTypes.Adjusted, new(OnHand: a.Delta, Damaged: damaged, Lost: lost - lostBack), RefTypes.Adjustment, adjustmentId, now);
            if (a.Delta > 0) await allocator.RunAsync([a.SkuId], now, ct);

            audit.Record(caller.UserId, caller.Role!, "inventory.stock-adjusted", "adjustment", adjustmentId.ToString(),
                new { skuId = a.SkuId, delta = a.Delta, reasonCode = a.Reason, serial });
            metrics.Adjustment(a.Reason);
            return new AdjustStockResponse { AdjustmentId = adjustmentId.ToString() };
        });
    }

    /// <summary>R15 checks that need no database state: ids, delta, reason for the direction, note, and the shapes of the serial fields.</summary>
    private static Adjustment ValidateAdjustment(AdjustStockRequest r)
    {
        var skuId = Inputs.Id(r.SkuId, "sku_id");
        if (r.Delta is 0 or int.MinValue) throw InventoryErrors.Validation("delta", "The delta must not be zero or int.MinValue."); // |int.MinValue| overflows
        if (!(r.Delta < 0 ? DecreaseReasons : IncreaseReasons).Contains(r.ReasonCode))
            throw InventoryErrors.Validation("reason_code", $"The reason code is not allowed for {(r.Delta < 0 ? "a decrease" : "an increase")}.");
        var note = r.Note.Trim();
        if (note.Length is 0 or > MaxNoteLength) throw InventoryErrors.Validation("note", $"The note is required, at most {MaxNoteLength} characters.");

        string? serial = null, newSerial = null;
        if (r.Serial.Length > 0)
            serial = Inputs.Serial(r.Serial, "serial");
        if (r.NewSerials.Count > 1) throw InventoryErrors.Validation("new_serials", "A serialized adjustment adds one unit per call.");
        if (r.NewSerials.Count == 1)
            newSerial = Inputs.Serial(r.NewSerials[0], "new_serials");
        if (r.NewImeis.Count > 0 && r.NewImeis.Count != r.NewSerials.Count) throw InventoryErrors.Validation("new_imeis", "There must be one IMEI per new serial, or none.");
        if (r.NewImeis.Count == 1 && !Identifiers.IsValidImei(r.NewImeis[0])) throw InventoryErrors.Validation("new_imeis", "An IMEI is 15 digits with a valid check digit.");
        if (serial is not null && newSerial is not null) throw InventoryErrors.Validation("new_serials", "Name either serial or new_serials, not both.");
        if ((serial is not null || newSerial is not null) && Math.Abs(r.Delta) != 1) throw InventoryErrors.Validation("delta", "A serialized adjustment moves one unit per call.");
        if (newSerial is not null && r.Delta < 0) throw InventoryErrors.Validation("new_serials", "A decrease names an existing serial.");
        return new(skuId, r.Delta, r.ReasonCode, note, serial, newSerial, r.NewImeis.Count == 1 ? r.NewImeis[0] : null);
    }

    /// <summary>Spec §4 "Serialized adjustments" and owner item 3, once the SKU's <c>serialized</c> flag is known.</summary>
    private static void CheckSerialShape(Adjustment a, bool serialized)
    {
        if (!serialized)
        {
            if (a.Serial is not null) throw InventoryErrors.Validation("serial", "The SKU is not serialized.");
            if (a.NewSerial is not null) throw InventoryErrors.Validation("new_serials", "The SKU is not serialized.");
            return;
        }
        if (Math.Abs(a.Delta) != 1) throw InventoryErrors.Validation("delta", "A serialized SKU adjusts one unit per call.");
        if (a.Delta < 0)
        {
            if (a.Serial is null) throw InventoryErrors.Validation("serial", "Name the in-stock serial to remove.");
            if (a.Reason is not (ReasonCodes.DamagedInWarehouse or ReasonCodes.LostInWarehouse))
                throw InventoryErrors.Validation("reason_code", "A serialized decrease is damaged_in_warehouse or lost_in_warehouse.");
        }
        else if ((a.Serial is null) == (a.NewSerial is null)) throw InventoryErrors.Validation("serial", "Name exactly one of serial (a lost unit) or new_serials.");
    }
}
