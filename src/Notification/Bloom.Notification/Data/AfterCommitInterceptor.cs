using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bloom.Notification.Data;

/// <summary>
/// Runs <see cref="NotificationDbContext.AfterCommit"/> once the transaction has committed and drops it on a rollback (plan N7).
/// A failing action is a warning: the work is best effort after the commit (LLD 09-notification §6.1).
/// </summary>
public sealed class AfterCommitInterceptor(ILogger<AfterCommitInterceptor> logger) : DbTransactionInterceptor
{
    // CancellationToken.None: a client that gives up after the commit must not lose the push.
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not NotificationDbContext db) return;
        var actions = db.AfterCommit.ToArray();
        db.AfterCommit.Clear();
        foreach (var action in actions)
        {
            try { await action(); }
            catch (Exception e) { logger.LogWarning(e, "An after-commit action failed"); }
        }
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as NotificationDbContext)?.AfterCommit.Clear();
        return Task.CompletedTask;
    }
}
