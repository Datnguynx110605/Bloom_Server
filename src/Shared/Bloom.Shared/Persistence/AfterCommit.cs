using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Bloom.Shared.Persistence;

/// <summary>A DbContext that owns the actions to run once its current transaction has committed (R3).</summary>
public interface IAfterCommitContext
{
    AfterCommitActions AfterCommit { get; }
}

/// <summary>Work that must run only after the transaction commits (pushes, metrics, notifications). Cleared on a rollback and when a new transaction begins.</summary>
public sealed class AfterCommitActions
{
    private readonly List<Func<Task>> _actions = [];

    public void Add(Func<Task> action) => _actions.Add(action);

    internal Func<Task>[] TakeAll()
    {
        var all = _actions.ToArray();
        _actions.Clear();
        return all;
    }

    internal void Clear() => _actions.Clear();
}

/// <summary>
/// Feeds <see cref="IAfterCommitContext.AfterCommit"/>. A failing action is a warning, never a failure of the committed work
/// (it would cause a redelivery). Every caller commits through EF's async transaction API.
/// </summary>
public sealed class AfterCommitInterceptor(ILogger<AfterCommitInterceptor> logger) : DbTransactionInterceptor
{
    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        (eventData.Context as IAfterCommitContext)?.AfterCommit.Clear(); // whatever an earlier, rolled-back transaction left behind
        return new(result);
    }

    // The actions take no token: a client that gives up after the commit must not lose them.
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not IAfterCommitContext context) return;
        foreach (var action in context.AfterCommit.TakeAll())
        {
            try { await action(); }
            catch (Exception e) { logger.LogWarning(e, "An after-commit action failed"); }
        }
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        (eventData.Context as IAfterCommitContext)?.AfterCommit.Clear();
        return Task.CompletedTask;
    }
}
