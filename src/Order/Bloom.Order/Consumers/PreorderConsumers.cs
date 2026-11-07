using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Order.Consumers;

/// <summary>
/// <c>inventory.stock-allocated.v1</c> (03-order §5.3, §6.5): <c>stock_allocated = true</c> on an <c>awaiting_stock</c> order, then the release check (OR-07). Any other status, or an unknown order: nothing.
/// </summary>
public sealed class StockAllocatedConsumer(OrderDbContext db, Transitions transitions, TimeProvider clock) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("inventory", InventoryMessages.StockAllocated)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var allocated = envelope.GetPayload<StockAllocatedV1>();
        if (allocated.OrderId == Guid.Empty) throw new PermanentFailureException("inventory.stock-allocated.v1 needs an order id.");

        var now = clock.GetUtcNow();
        var flagged = await db.Orders.Where(o => o.Id == allocated.OrderId && o.Status == OrderStatuses.AwaitingStock && !o.StockAllocated)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.StockAllocated, true).SetProperty(o => o.Version, o => o.Version + 1).SetProperty(o => o.UpdatedAt, now), ct);
        if (flagged == 1) await transitions.PromoteIfReadyAsync(allocated.OrderId, ct);
    }
}

/// <summary>
/// <c>launch.stage-changed.v1</c> (03-order §5.3, §6.5, spec §4, §7): on <c>released</c>, sets <c>launch_released</c> on the launch's open orders in batches of 500, so a launch of thousands
/// is not one transaction. Each batch has its own scope and transaction, committed before the next; <c>ConsumerHost</c>'s transaction only holds the inbox row. A redelivery after a crash
/// selects only orders not yet flagged, so every order gets one <c>ready-to-fulfill</c> or one <c>preorder-delayed</c>.
/// </summary>
public sealed class LaunchStageChangedConsumer(IServiceScopeFactory scopes) : IMessageHandler
{
    private const int BatchSize = 500;

    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("launch", LaunchMessages.StageChanged)];

    private sealed record Flagged(Guid Id, string Status, bool StockAllocated, string Number, Guid AccountId);

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var stage = envelope.GetPayload<LaunchStageChangedV1>();
        if (stage.LaunchId == Guid.Empty) throw new PermanentFailureException("launch.stage-changed.v1 needs a launch id.");
        if (stage.Stage != "released") return;

        while (await BatchAsync(stage.LaunchId, ct) == BatchSize) { }
    }

    private async Task<int> BatchAsync(Guid launchId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var transitions = scope.ServiceProvider.GetRequiredService<Transitions>();
        var outbox = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.Database.SqlQuery<Flagged>($"""
            UPDATE orders SET launch_released = true, version = version + 1, updated_at = {now}
            WHERE id IN (SELECT id FROM orders
                         WHERE kind = 'launch' AND launch_id = {launchId} AND status IN ('creating', 'pending_payment', 'awaiting_stock') AND NOT launch_released
                         ORDER BY id LIMIT {BatchSize} FOR UPDATE)
            RETURNING id AS "Id", status AS "Status", stock_allocated AS "StockAllocated", number AS "Number", account_id AS "AccountId"
            """).ToListAsync(ct);
        foreach (var order in batch.Where(o => o.Status == OrderStatuses.AwaitingStock))
        {
            if (order.StockAllocated) await transitions.PromoteIfReadyAsync(order.Id, ct);
            else outbox.PublishEvent(OrderMessages.PreorderDelayed, new OrderPreorderDelayedV1(order.Id, order.Number, order.AccountId));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }
}
