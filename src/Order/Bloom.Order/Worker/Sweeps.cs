using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Order.Observability;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Worker;

/// <summary>The creating and expiry sweeps (03-order §6.9, plan R14), every 10 s.</summary>
public sealed class Sweeps(IServiceScopeFactory scopes, TimeProvider clock, OrderMetrics metrics, ILogger<Sweeps> logger) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), clock);
        do
        {
            await PassAsync("Creating", RunCreatingAsync, ct);
            await PassAsync("Expiry", RunExpiryAsync, ct);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    // One failing pass must not skip the other; its batch rolled back, and the next tick retries it.
    private async Task PassAsync(string name, Func<CancellationToken, Task<int>> pass, CancellationToken ct)
    {
        try
        {
            await pass(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{Sweep} sweep failed; it runs again on its next tick", name);
        }
    }

    /// <summary><c>creating</c> orders past their deadline: <c>cancelled</c> (<c>checkout_failed</c>), the compensation saga and <c>order.cancelled.v1</c>. Returns how many it cancelled.</summary>
    internal Task<int> RunCreatingAsync(CancellationToken ct) =>
        RunAsync("Creating", OrderStatuses.Creating, async (scope, order) => await scope.GetRequiredService<CheckoutFailures>().CancelAsync(order, compensate: true, ct) ? 1 : 0, ct);

    /// <summary><c>pending_payment</c> orders past their deadline: <c>cancelled</c> (<c>checkout_expired</c>), the coupon released, the <c>expiry</c> saga with its commands, and <c>order.cancelled.v1</c>. Returns how many it expired.</summary>
    internal Task<int> RunExpiryAsync(CancellationToken ct) =>
        RunAsync("Expiry", OrderStatuses.PendingPayment, async (scope, order) =>
        {
            var moved = await scope.GetRequiredService<Transitions>().MoveAsync(order.Id, [OrderStatuses.PendingPayment], OrderStatuses.Cancelled, Actor.System, CancelReasons.CheckoutExpired, null,
                s => s.SetProperty(o => o.CancelReason, CancelReasons.CheckoutExpired), ct);
            if (!moved) return 0;
            await scope.GetRequiredService<Coupons>().MoveUseAsync(order.Id, CouponUseStatuses.Held, CouponUseStatuses.Released, ct);
            await scope.GetRequiredService<Sagas>().StartAsync(order.Id, SagaTypes.Expiry, order.Kind, CancelReasons.CheckoutExpired, ct);
            scope.GetRequiredService<OutboxWriter>().PublishEvent(OrderMessages.Cancelled, new OrderCancelledV1(order.Id, order.Number, order.AccountId, CancelReasons.CheckoutExpired));
            return 1;
        }, ct, metrics.CheckoutExpired);

    /// <summary>
    /// Batches of 100 orders in <paramref name="status"/> past their deadline, each locked <c>FOR UPDATE SKIP LOCKED</c> and handled in one transaction, until a batch comes back short.
    /// Each order runs in its own savepoint: one that throws is rolled back, logged and left for the next tick, and the rest of the batch commits (O-2).
    /// </summary>
    private async Task<int> RunAsync(string sweep, string status, Func<IServiceProvider, OrderRow, Task<int>> handle, CancellationToken ct, Action<int>? committed = null)
    {
        var total = 0;
        var failed = new List<Guid>(); // not fetched again by this run
        int locked;
        do
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var skip = failed.ToArray();
            var batch = await db.Orders.FromSql($"""
                SELECT * FROM orders WHERE status = {status} AND deadline_at < {clock.GetUtcNow()} AND id <> ALL({skip}) ORDER BY deadline_at LIMIT {BatchSize} FOR UPDATE SKIP LOCKED
                """).AsNoTracking().ToListAsync(ct);
            var done = 0;
            foreach (var order in batch)
            {
                await tx.CreateSavepointAsync("order_step", ct);
                try
                {
                    var handled = await handle(scope.ServiceProvider, order);
                    await db.SaveChangesAsync(ct);
                    done += handled;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    await tx.RollbackToSavepointAsync("order_step", ct);
                    db.ChangeTracker.Clear(); // the outbox rows the failed order queued must not be saved with the next one
                    failed.Add(order.Id);
                    logger.LogError(ex, "{Sweep} sweep failed for order {OrderId}; it is retried on the next tick", sweep, order.Id);
                }
            }
            await tx.CommitAsync(ct);
            committed?.Invoke(done); // counted once the batch committed
            total += done;
            locked = batch.Count;
        }
        while (locked == BatchSize);
        return total;
    }
}
