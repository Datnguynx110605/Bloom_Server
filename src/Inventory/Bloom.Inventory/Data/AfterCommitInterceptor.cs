using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bloom.Inventory.Data;

/// <summary>Feeds <see cref="InventoryDbContext.AfterCommit"/>. Every caller (consumers, the idempotency guard, the stock API) commits through EF's async transaction API.</summary>
public sealed class AfterCommitInterceptor : DbTransactionInterceptor
{
    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        (eventData.Context as InventoryDbContext)?.DropAfterCommit(); // whatever an earlier, rolled-back transaction left behind
        return new(result);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as InventoryDbContext)?.RunAfterCommit();
        return Task.CompletedTask;
    }
}
