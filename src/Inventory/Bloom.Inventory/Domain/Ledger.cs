using Bloom.Inventory.Data;
using Bloom.Inventory.Observability;

namespace Bloom.Inventory.Domain;

/// <summary>Change to each counter of a <c>stock_items</c> row (the <c>d_*</c> ledger columns).</summary>
public readonly record struct Deltas(int OnHand = 0, int Reserved = 0, int Committed = 0, int Returned = 0, int Damaged = 0, int Lost = 0);

/// <summary>Writes the append-only <c>stock_ledger</c> (IR-03, plan R11): one row per SKU per change, saved with the caller's transaction.</summary>
public sealed class Ledger(InventoryDbContext db, TimeProvider clock, InventoryMetrics metrics)
{
    /// <summary>The staff id (<c>D</c> form) for Warehouse API writes, <c>inventory</c> otherwise.</summary>
    public string Actor { get; set; } = "inventory";

    /// <param name="refType">A <see cref="RefTypes"/> value.</param>
    public void Add(Guid skuId, string entryType, Deltas d, string refType, Guid refId)
    {
        db.StockLedger.Add(new LedgerEntry
        {
            Id = Guid.CreateVersion7(), SkuId = skuId, EntryType = entryType,
            DOnHand = d.OnHand, DReserved = d.Reserved, DCommitted = d.Committed, DReturned = d.Returned, DDamaged = d.Damaged, DLost = d.Lost,
            RefType = refType, RefId = refId, Actor = Actor, CreatedAt = clock.GetUtcNow(),
        });
        db.AfterCommit.Add(() => { metrics.LedgerEntry(entryType); return Task.CompletedTask; });
    }
}
