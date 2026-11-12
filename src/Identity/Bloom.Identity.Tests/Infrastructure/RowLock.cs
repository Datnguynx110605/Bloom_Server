using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Bloom.Identity.Tests;

/// <summary>A row lock held on a separate connection, so a test can start operations, see them block, and release them together.</summary>
public sealed class RowLock : IAsyncDisposable
{
    private readonly IdentityDbContext _db;
    private readonly IDbContextTransaction _tx;

    private RowLock(IdentityDbContext db, IDbContextTransaction tx) => (_db, _tx) = (db, tx);

    /// <param name="forUpdate">A <c>SELECT … FOR UPDATE</c> naming the rows to hold.</param>
    public static async Task<RowLock> HoldAsync(TestIdentity t, FormattableString forUpdate)
    {
        var db = t.NewDb();
        var tx = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(forUpdate, TestContext.Current.CancellationToken);
        return new RowLock(db, tx);
    }

    /// <summary>Runs a statement inside the held transaction, as the lock holder's own write.</summary>
    public Task ExecuteAsync(FormattableString sql) => _db.Database.ExecuteSqlInterpolatedAsync(sql, TestContext.Current.CancellationToken);

    public Task ReleaseAsync() => _tx.CommitAsync(TestContext.Current.CancellationToken);

    /// <summary>Waits until <paramref name="count"/> backends wait on a lock.</summary>
    public static Task WaitForWaitersAsync(TestIdentity t, int count, string what) => Poll.UntilAsync(async () =>
    {
        await using var db = t.NewDb(); // a fresh transaction: pg_stat_activity is a per-transaction snapshot
        return (await db.Database.SqlQuery<string>(
            $"SELECT wait_event_type AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
            .ToListAsync(TestContext.Current.CancellationToken)).Count >= count;
    }, 10, what);

    public async ValueTask DisposeAsync()
    {
        await _tx.DisposeAsync();
        await _db.DisposeAsync();
    }
}
