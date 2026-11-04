using Bloom.Inventory.Context;
using Bloom.Inventory.Data;
using Bloom.Inventory.Domain;
using Bloom.Inventory.Errors;
using Bloom.Inventory.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using LedgerRow = Bloom.Inventory.Data.LedgerEntry;

namespace Bloom.Inventory.Services;

/// <summary>02-inventory §5.1 reads: role first (R7), then validation (R15, R19). No idempotency key and no write transaction.</summary>
public sealed partial class WarehouseService
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    public override async Task<ListStockLevelsResponse> ListStockLevels(ListStockLevelsRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.StockViewers);
        var ct = context.CancellationToken;
        var (skip, take) = Paging(request.Page, request.PageSize);
        Guid? productId = request.HasProductId ? Inputs.Id(request.ProductId, "product_id") : null;
        var skuIds = request.SkuIds.Select(id => Inputs.Id(id, "sku_ids")).ToList();
        if (request.HasAvailableAtMost && request.AvailableAtMost < 0) throw InventoryErrors.Validation("available_at_most", "The value must not be negative.");

        var items = db.StockItems.AsNoTracking();
        if (productId is { } product) items = items.Where(s => s.ProductId == product);
        if (skuIds.Count > 0) items = items.Where(s => skuIds.Contains(s.SkuId));
        if (request.HasAvailableAtMost) items = items.Where(s => s.OnHand - s.Reserved <= request.AvailableAtMost);
        var rows = items.Select(s => new
        {
            Item = s,
            Backordered = db.StockAllocations.Where(a => a.SkuId == s.SkuId && a.Status == AllocationStatuses.Backordered).Sum(a => (int?)a.Quantity) ?? 0,
        });
        if (request.HasHasBackorders) rows = rows.Where(r => (r.Backordered > 0) == request.HasBackorders);

        var page = await rows.OrderBy(r => r.Item.SkuId).Skip(skip).Take(take).ToListAsync(ct);
        var response = new ListStockLevelsResponse();
        response.Items.AddRange(page.Select(r => new StockLevel
        {
            SkuId = r.Item.SkuId.ToString(), ProductId = r.Item.ProductId.ToString(), Serialized = r.Item.Serialized,
            OnHand = r.Item.OnHand, Reserved = r.Item.Reserved, Available = r.Item.OnHand - r.Item.Reserved, Committed = r.Item.Committed,
            Returned = r.Item.Returned, Damaged = r.Item.Damaged, Lost = r.Item.Lost, Backordered = r.Backordered,
            UpdatedAt = Timestamp.FromDateTimeOffset(r.Item.UpdatedAt),
        }));
        return response;
    }

    public override async Task<GetLedgerResponse> GetLedger(GetLedgerRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.LedgerReaders);
        var ct = context.CancellationToken;
        var (skip, take) = Paging(request.Page, request.PageSize);
        var skuId = Inputs.Id(request.SkuId, "sku_id");

        var entries = db.StockLedger.AsNoTracking().Where(e => e.SkuId == skuId);
        if (request.From is { } from) entries = entries.Where(e => e.CreatedAt >= from.ToDateTimeOffset());
        if (request.To is { } to) entries = entries.Where(e => e.CreatedAt < to.ToDateTimeOffset());

        var page = await entries.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Skip(skip).Take(take).ToListAsync(ct);
        var response = new GetLedgerResponse();
        response.Entries.AddRange(page.Select(ToView));
        return response;
    }

    public override async Task<SerialUnitView> GetSerial(GetSerialRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.SerialReaders);
        var serial = Inputs.Serial(request.Serial, "serial");
        var unit = await db.SerialUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Serial == serial, context.CancellationToken)
            ?? throw InventoryErrors.Error(InventoryErrors.NotFound, "No such serial.");
        return new SerialUnitView
        {
            Serial = unit.Serial, Imei = unit.Imei ?? "", SkuId = unit.SkuId.ToString(), Status = unit.Status, RefId = unit.RefId?.ToString() ?? "",
            ReceiptId = unit.ReceiptId.ToString(), UpdatedAt = Timestamp.FromDateTimeOffset(unit.UpdatedAt),
        };
    }

    /// <summary>R19: <c>page</c> 0 is 1, <c>page_size</c> 0 is 50 and at most 100. Returns the row offset (capped at <c>int.MaxValue</c>, past any real table) and the page size.</summary>
    private static (int Skip, int Take) Paging(int page, int pageSize)
    {
        if (page < 0) throw InventoryErrors.Validation("page", "The page must not be negative.");
        if (pageSize is < 0 or > MaxPageSize) throw InventoryErrors.Validation("page_size", $"The page size is 1 to {MaxPageSize}.");
        var size = pageSize == 0 ? DefaultPageSize : pageSize;
        return ((int)Math.Min((Math.Max(page, 1) - 1L) * size, int.MaxValue), size);
    }

    private static V1.LedgerEntry ToView(LedgerRow e) => new()
    {
        Id = e.Id.ToString(), SkuId = e.SkuId.ToString(), EntryType = e.EntryType,
        DOnHand = e.DOnHand, DReserved = e.DReserved, DCommitted = e.DCommitted, DReturned = e.DReturned, DDamaged = e.DDamaged, DLost = e.DLost,
        RefType = e.RefType, RefId = e.RefId.ToString(), Actor = e.Actor, CreatedAt = Timestamp.FromDateTimeOffset(e.CreatedAt),
    };
}
