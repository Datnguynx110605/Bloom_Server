using Bloom.Catalog.Contracts;
using Bloom.Inventory.Context;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Inventory.Errors;
using Bloom.Inventory.V1;
using Bloom.Shared.Idempotency;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Inventory.Services;

/// <summary>02-inventory §3 component <c>Warehouse API</c>: staff only, transcoded to JSON under <c>/api/admin/inventory</c>.</summary>
public sealed partial class WarehouseService(
    InventoryDbContext db, IdempotencyGuard guard, StockLocks locks, Ledger ledger, Allocator allocator, Audit audit,
    CatalogInternal.CatalogInternalClient catalog, TimeProvider clock) : InventoryWarehouse.InventoryWarehouseBase
{
    private const int MaxNoteLength = 500;
    private static readonly TimeSpan CatalogDeadline = TimeSpan.FromSeconds(2);

    private sealed record Line(Guid SkuId, int Quantity, List<string> Serials, List<string> Imeis);

    /// <summary>02-inventory §6.3 in the order of spec §4: role, then (a) checks that don't need <c>serialized</c>, (b) catalog lock for new SKUs, (c) checks that do, then one guarded transaction.</summary>
    public override async Task<RecordGoodsReceiptResponse> RecordGoodsReceipt(RecordGoodsReceiptRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.StockWriters);
        var ct = context.CancellationToken;
        if (request.Lines.Count == 0) throw InventoryErrors.Validation("lines", "A receipt needs at least one line.");
        if (request.Note.Length > MaxNoteLength) throw InventoryErrors.Validation("note", $"The note is at most {MaxNoteLength} characters.");

        var violations = new List<(string Field, string Description)>();
        var lines = await CheckFormatAsync(request, violations, ct);
        Dictionary<Guid, StockItem> known = [];
        Dictionary<Guid, LockSkuForStockResponse> fromCatalog = [];
        if (violations.Count == 0)
        {
            var ids = lines.Select(l => l.SkuId).ToList();
            known = await db.StockItems.AsNoTracking().Where(s => ids.Contains(s.SkuId)).ToDictionaryAsync(s => s.SkuId, ct);
            var unknown = new HashSet<int>();
            for (var i = 0; i < lines.Count; i++)
            {
                if (known.ContainsKey(lines[i].SkuId)) continue;
                if (await LockInCatalogAsync(lines[i].SkuId, ct) is { } response) fromCatalog[lines[i].SkuId] = response;
                else unknown.Add(i);
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (unknown.Contains(i)) { violations.Add(($"lines[{i}].sku_id", "The SKU doesn't exist in the catalog.")); continue; }
                var serialized = known.TryGetValue(line.SkuId, out var item) ? item.Serialized : fromCatalog[line.SkuId].Serialized;
                if (serialized && line.Serials.Count != line.Quantity)
                    violations.Add(($"lines[{i}].serials", $"A serialized SKU needs exactly {line.Quantity} serials."));
                if (!serialized && line.Serials.Count > 0) violations.Add(($"lines[{i}].serials", "The SKU is not serialized."));
                if (!serialized && line.Imeis.Count > 0) violations.Add(($"lines[{i}].imeis", "The SKU is not serialized."));
            }
        }
        if (violations.Count > 0) throw InventoryErrors.ReceiptInvalid(violations);

        return await guard.ExecuteAsync(request, context, async _ =>
        {
            var now = clock.GetUtcNow();
            var sorted = lines.OrderBy(l => l.SkuId).ToList();
            foreach (var line in sorted.Where(l => !known.ContainsKey(l.SkuId)))
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO stock_items (sku_id, product_id, serialized, on_hand, reserved, committed, returned, damaged, lost, updated_at)
                    VALUES ({line.SkuId}, {Guid.Parse(fromCatalog[line.SkuId].ProductId)}, {fromCatalog[line.SkuId].Serialized}, 0, 0, 0, 0, 0, 0, {now})
                    ON CONFLICT DO NOTHING
                    """, ct);
            await locks.LockAsync(sorted.Select(l => l.SkuId), ct);

            var receiptId = Guid.CreateVersion7();
            db.GoodsReceipts.Add(new GoodsReceipt { Id = receiptId, StaffId = caller.UserId, Note = request.Note, ReceivedAt = now });
            foreach (var line in sorted)
            {
                db.GoodsReceiptLines.Add(new GoodsReceiptLine { ReceiptId = receiptId, SkuId = line.SkuId, Quantity = line.Quantity });
                for (var j = 0; j < line.Serials.Count; j++)
                    db.SerialUnits.Add(new SerialUnit
                    {
                        Serial = line.Serials[j], Imei = line.Imeis.Count > 0 ? line.Imeis[j] : null, SkuId = line.SkuId,
                        Status = SerialStatuses.InStock, ReceiptId = receiptId, UpdatedAt = now,
                    });
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "serial_units" } pg)
            {
                // A concurrent receipt won the race for a serial or IMEI: the guard rolls this one back and stores the rejection.
                throw InventoryErrors.ReceiptInvalid([("lines", $"A serial or IMEI was received concurrently ({pg.ConstraintName}).")]);
            }

            ledger.Actor = caller.UserId.ToString("D");
            foreach (var line in sorted)
            {
                await db.Database.ExecuteSqlAsync($"UPDATE stock_items SET on_hand = on_hand + {line.Quantity}, updated_at = {now} WHERE sku_id = {line.SkuId}", ct);
                ledger.Add(line.SkuId, EntryTypes.Received, new(OnHand: line.Quantity), RefTypes.Receipt, receiptId);
            }
            foreach (var line in sorted) await allocator.RunAsync(line.SkuId, ct);

            audit.Record(caller.UserId, caller.Role!, "inventory.goods-received", "receipt", receiptId.ToString(),
                new { lines = sorted.Select(l => new { skuId = l.SkuId, quantity = l.Quantity }) });
            return new RecordGoodsReceiptResponse { ReceiptId = receiptId.ToString() };
        });
    }

    /// <summary>Step (a): every check that doesn't need <c>serialized</c>, in request order. Returns the parsed lines, usable only while <paramref name="violations"/> stays empty.</summary>
    private async Task<List<Line>> CheckFormatAsync(RecordGoodsReceiptRequest request, List<(string Field, string Description)> violations, CancellationToken ct)
    {
        var serials = request.Lines.Select(l => l.Serials.Select(Identifiers.NormalizeSerial).OfType<string>().ToList()).ToList();
        var imeis = request.Lines.SelectMany(l => l.Imeis).Where(Identifiers.IsValidImei).Distinct().ToList();
        var allSerials = serials.SelectMany(s => s).Distinct().ToList();
        var held = await db.SerialUnits.AsNoTracking().Where(u => allSerials.Contains(u.Serial) || (u.Imei != null && imeis.Contains(u.Imei)))
            .Select(u => new { u.Serial, u.Imei }).ToListAsync(ct);
        var heldSerials = held.Select(h => h.Serial).ToHashSet();
        var heldImeis = held.Select(h => h.Imei).OfType<string>().ToHashSet();

        var skus = new HashSet<Guid>();
        var seenSerials = new HashSet<string>();
        var seenImeis = new HashSet<string>();
        var parsed = new List<Line>();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            var ok = Guid.TryParseExact(line.SkuId, "D", out var skuId);
            if (!ok) violations.Add(($"lines[{i}].sku_id", "The SKU id must be a UUID."));
            else if (!skus.Add(skuId)) violations.Add(($"lines[{i}].sku_id", "The SKU appears on more than one line."));
            if (line.Quantity <= 0) violations.Add(($"lines[{i}].quantity", "The quantity must be positive."));

            var normalized = new List<string>();
            for (var j = 0; j < line.Serials.Count; j++)
            {
                var serial = Identifiers.NormalizeSerial(line.Serials[j]);
                if (serial is null) violations.Add(($"lines[{i}].serials[{j}]", "A serial is 1-32 letters or digits."));
                else if (!seenSerials.Add(serial)) violations.Add(($"lines[{i}].serials[{j}]", "The serial is repeated in this receipt."));
                else if (heldSerials.Contains(serial)) violations.Add(($"lines[{i}].serials[{j}]", "The serial already exists."));
                if (serial is not null) normalized.Add(serial);
            }
            for (var j = 0; j < line.Imeis.Count; j++)
            {
                var imei = line.Imeis[j];
                if (!Identifiers.IsValidImei(imei)) violations.Add(($"lines[{i}].imeis[{j}]", "An IMEI is 15 digits with a valid check digit."));
                else if (!seenImeis.Add(imei)) violations.Add(($"lines[{i}].imeis[{j}]", "The IMEI is repeated in this receipt."));
                else if (heldImeis.Contains(imei)) violations.Add(($"lines[{i}].imeis[{j}]", "The IMEI already exists."));
            }
            if (line.Imeis.Count > 0 && line.Imeis.Count != line.Serials.Count)
                violations.Add(($"lines[{i}].imeis", "There must be one IMEI per serial, or none."));
            parsed.Add(new Line(skuId, line.Quantity, normalized, [.. line.Imeis])); // skuId is empty when it didn't parse; the violation stops the receipt
        }
        return parsed;
    }

    /// <summary>Step (b): the catalog locks the SKU's <c>serialized</c> flag (idempotent). Null when the catalog doesn't know the SKU.</summary>
    /// <exception cref="RpcException"><c>CATALOG_UNAVAILABLE</c> on any other failure.</exception>
    private async Task<LockSkuForStockResponse?> LockInCatalogAsync(Guid skuId, CancellationToken ct)
    {
        try
        {
            return await catalog.LockSkuForStockAsync(new LockSkuForStockRequest { SkuId = skuId.ToString("D") },
                deadline: DateTime.UtcNow + CatalogDeadline, cancellationToken: ct);
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
        catch (RpcException e) when (e.StatusCode != StatusCode.Cancelled)
        {
            throw InventoryErrors.CatalogUnavailable();
        }
    }
}
