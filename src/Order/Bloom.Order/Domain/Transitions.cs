using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Errors;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Bloom.Order.Domain;

/// <param name="Type"><c>customer</c>, <c>staff</c> or <c>system</c>.</param>
public sealed record Actor(string Type, Guid? Id)
{
    public static readonly Actor System = new(ActorTypes.System, null);
}

/// <summary>The one place an order's status changes (plan R8, spec §7): a guarded update and its history row (ORD-R04) in the caller's transaction.</summary>
public sealed class Transitions(OrderDbContext db, OutboxWriter outbox)
{
    internal sealed class Current
    {
        public string Status { get; set; } = "";
        public long Version { get; set; }
        public DateTimeOffset Now { get; set; }
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

        var current = (await db.Database.SqlQuery<Current>($"SELECT status AS \"Status\", version AS \"Version\", now() AS \"Now\" FROM orders WHERE id = {orderId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (current is null || !from.Contains(current.Status)) return false;
        if (version is { } expected && expected != current.Version)
            throw OrderErrors.Conflict("order");

        return await MoveLockedAsync(orderId, current.Status, to, actor, reason, current.Now, extra, ct);
    }

    /// <summary>
    /// The guarded update and history row of <see cref="MoveAsync"/>, for a caller that already holds the row <c>FOR UPDATE</c> in the open transaction and has read the order's
    /// <paramref name="from"/> status and the database <paramref name="now"/> under that lock. The lock is what keeps the history row's <c>from_status</c> exact: without it, use
    /// <see cref="MoveAsync"/>. Returns false when the guarded update affects no row.
    /// </summary>
    public async Task<bool> MoveLockedAsync(Guid orderId, string from, string to, Actor actor, string? reason, DateTimeOffset now, Action<UpdateSettersBuilder<OrderRow>>? extra, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A status change and its history row commit together: open a transaction first.");

        var affected = await db.Orders.Where(o => o.Id == orderId && o.Status == from).ExecuteUpdateAsync(s =>
        {
            s.SetProperty(o => o.Status, to).SetProperty(o => o.Version, o => o.Version + 1).SetProperty(o => o.UpdatedAt, now);
            extra?.Invoke(s);
        }, ct);
        if (affected != 1) return false;

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO order_status_history (order_id, from_status, to_status, actor_type, actor_id, reason, at)
            VALUES ({orderId}, {from}, {to}, {actor.Type}, {actor.Id}, {reason}, {now})
            """, ct);
        return true;
    }

    /// <summary>
    /// 03-order §6.5 (OR-07): <c>awaiting_stock</c> → <c>paid</c> once both <c>stock_allocated</c> and <c>launch_released</c> are true, handing the order to fulfillment with one
    /// <c>order.ready-to-fulfill.v1</c>. Both flags only ever turn true, so a read of true holds; the guarded move still decides on the status. Returns false when the order is not ready.
    /// </summary>
    public async Task<bool> PromoteIfReadyAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId && o.StockAllocated && o.LaunchReleased, ct);
        if (order is null || !await MoveAsync(orderId, [OrderStatuses.AwaitingStock], OrderStatuses.Paid, Actor.System, null, null, s => s.SetProperty(o => o.HandedToFulfillment, true), ct)) return false;

        var lines = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == orderId).OrderBy(l => l.LineNo).ToListAsync(ct);
        outbox.PublishEvent(OrderMessages.ReadyToFulfill, ReadyToFulfill(order, lines));
        return true;
    }

    internal static OrderReadyToFulfillV1 ReadyToFulfill(OrderRow order, IEnumerable<OrderLine> lines) =>
        new(order.Id, order.AccountId, order.Number, order.ShipTo, order.ShippingOption, order.ShippingCents, order.TotalCents,
            [.. lines.Select(l => new ReadyLineV1(l.LineNo, l.Kind, l.SkuId, l.ProductId, l.Serialized, l.Quantity, l.ParentLineNo, l.CoverageMonths, l.PaidCents, l.TaxCents))]);
}
