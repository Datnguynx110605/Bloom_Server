using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Observability;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Worker;

/// <summary>
/// Every minute (05-fulfillment §10, plan R22): sets <c>pack_tasks_by_status</c>, <c>returns_by_status</c> and <c>possibly_lost_open</c>, and logs one error
/// when refund sagas have been open for more than 30 minutes (the "refund saga stuck" alert). Reports only.
/// </summary>
public sealed class FulfillmentMonitor(IServiceScopeFactory scopes, FulfillmentMetrics metrics, TimeProvider clock, ILogger<FulfillmentMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Fulfillment monitor run failed; retrying in a minute");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
        var cutoff = await db.Database.NowAsync(ct) - StuckAfter;
        // Every status is reported, so one that has just emptied drops to 0 instead of keeping its last sample.
        var packTasks = TaskStatuses.All.ToDictionary(s => s, _ => 0L);
        foreach (var g in await db.PackTasks.AsNoTracking().GroupBy(p => p.Status).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct)) packTasks[g.Key] = g.Count;
        var returns = ReturnStatuses.All.ToDictionary(s => s, _ => 0L);
        foreach (var g in await db.ReturnRequests.AsNoTracking().GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(ct)) returns[g.Key] = g.Count;
        var lost = await db.DeliveryFailures.AsNoTracking().LongCountAsync(f => f.Status == FailureStatuses.PossiblyLost, ct);
        metrics.SetGauges(packTasks, returns, lost);

        var stuck = await db.FulfillmentSagas.AsNoTracking().Where(s => s.CompletedAt == null && s.StartedAt < cutoff).OrderBy(s => s.StartedAt)
            .Select(s => new { s.Type, s.RefId }).ToListAsync(ct);
        if (stuck.Count > 0)
            logger.LogError("{Count} fulfillment sagas have been open for more than 30 minutes: {Refs}", stuck.Count, string.Join(", ", stuck.Select(s => $"{s.Type}:{s.RefId}")));
    }
}
