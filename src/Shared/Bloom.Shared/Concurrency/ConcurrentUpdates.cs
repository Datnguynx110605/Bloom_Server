using Bloom.Shared.Errors;
using Bloom.Shared.Observability;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Bloom.Shared.Concurrency;

/// <summary>Atomic conditional updates (00-reliability §7.5) and optimistic locking (§7.6).</summary>
public sealed class ConcurrentUpdates(DbContext db, BloomMetrics metrics)
{
    /// <summary>Runs one guarded statement. True iff it affected one row; zero rows counts a rejection and returns false.</summary>
    public async Task<bool> TryConditionalUpdateAsync(string counter, FormattableString sql, CancellationToken ct = default)
    {
        var affected = await db.Database.ExecuteSqlAsync(sql, ct);
        if (affected == 1) return true;
        metrics.ConditionalRejection(counter);
        return false;
    }

    /// <summary>
    /// <c>UPDATE … SET …, version = version + 1 WHERE … AND version = @expected</c>. Returns the new version.
    /// Zero rows: the row exists → ABORTED/CONCURRENCY_CONFLICT, otherwise NOT_FOUND with <paramref name="notFoundReason"/>. No retry.
    /// </summary>
    public async Task<long> UpdateVersionedAsync<T>(string entity, IQueryable<T> row, long expectedVersion,
        Action<UpdateSettersBuilder<T>> set, string notFoundReason, CancellationToken ct = default) where T : class
    {
        var affected = await row.Where(x => EF.Property<long>(x, "Version") == expectedVersion).ExecuteUpdateAsync(b =>
        {
            set(b);
            b.SetProperty(x => EF.Property<long>(x, "Version"), x => EF.Property<long>(x, "Version") + 1);
        }, ct);
        if (affected == 1) return expectedVersion + 1;

        if (await row.AnyAsync(ct))
        {
            metrics.OptimisticConflict(entity);
            throw BloomErrors.Create(StatusCode.Aborted, BloomErrors.ConcurrencyConflict, $"{entity} was changed by someone else; reload and retry.");
        }
        throw BloomErrors.Create(StatusCode.NotFound, notFoundReason, $"{entity} not found.");
    }
}
