using Bloom.Order.Data;
using Bloom.Shared.Errors;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Bloom.Order.Domain;

/// <param name="Type"><c>customer</c>, <c>staff</c> or <c>system</c>.</param>
public sealed record Actor(string Type, Guid? Id)
{
    public static readonly Actor System = new(ActorTypes.System, null);
}

/// <summary>The one place an order's status changes (plan R8, spec §7): a guarded update and its history row (ORD-R04) in the caller's transaction.</summary>
public sealed class Transitions(OrderDbContext db, TimeProvider clock)
{
    internal sealed class Current
    {
        public string Status { get; set; } = "";
        public long Version { get; set; }
    }

    /// <summary>
    /// Moves the order from any of <paramref name="from"/> to <paramref name="to"/> and writes the history row at once (it is not left to <c>SaveChanges</c>).
    /// <paramref name="version"/>, when given, must match. Returns false when the order is missing or in another status (the caller maps the error);
    /// a stale <paramref name="version"/> on an order in a <paramref name="from"/> status is <c>CONCURRENCY_CONFLICT</c>. Needs an open transaction: the row lock taken here
    /// is what makes the history row's <c>from_status</c> exact when <paramref name="from"/> lists several statuses.
    /// </summary>
    /// <param name="extra">More column changes of the same <c>UPDATE</c>, for example the cancel reason or the totals.</param>
    public async Task<bool> MoveAsync(Guid orderId, string[] from, string to, Actor actor, string? reason, long? version, Action<UpdateSettersBuilder<OrderRow>>? extra, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A status change and its history row commit together: open a transaction first.");

        var current = (await db.Database.SqlQuery<Current>($"SELECT status AS \"Status\", version AS \"Version\" FROM orders WHERE id = {orderId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (current is null || !from.Contains(current.Status)) return false;
        if (version is { } expected && expected != current.Version)
            throw BloomErrors.Create(StatusCode.Aborted, BloomErrors.ConcurrencyConflict, "The order was changed by someone else; reload and retry.");

        var now = clock.GetUtcNow();
        var query = db.Orders.Where(o => o.Id == orderId && from.Contains(o.Status));
        if (version is { } v) query = query.Where(o => o.Version == v);
        var affected = await query.ExecuteUpdateAsync(s =>
        {
            s.SetProperty(o => o.Status, to).SetProperty(o => o.Version, o => o.Version + 1).SetProperty(o => o.UpdatedAt, now);
            extra?.Invoke(s);
        }, ct);
        if (affected != 1) return false;

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO order_status_history (order_id, from_status, to_status, actor_type, actor_id, reason, at)
            VALUES ({orderId}, {current.Status}, {to}, {actor.Type}, {actor.Id}, {reason}, {now})
            """, ct);
        return true;
    }
}
