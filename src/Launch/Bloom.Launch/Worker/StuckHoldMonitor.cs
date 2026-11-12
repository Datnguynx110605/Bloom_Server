using Bloom.Launch.Data;
using Bloom.Launch.Observability;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Worker;

/// <summary>01-launch §10 <c>stuck_holds</c>, every minute: <c>held</c> holds older than 30 minutes per launch (a stuck checkout saga in order). One error log per launch when a hold first becomes stuck (D7); afterwards the gauge carries it. Database <c>now()</c> decides the age (R6).</summary>
public sealed class StuckHoldMonitor(IServiceScopeFactory scopes, LaunchMetrics metrics, ILogger<StuckHoldMonitor> logger) : BackgroundService
{
    private sealed record Stuck(Guid Launch, Guid Hold);

    private Dictionary<Guid, Guid> _reported = []; // stuck hold → its launch, as of the last pass

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "The stuck-hold monitor pass failed.");
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        List<Stuck> stuck;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LaunchDbContext>();
            stuck = await db.Database.SqlQuery<Stuck>($"""
                SELECT launch_id AS "Launch", id AS "Hold" FROM holds
                WHERE status = {HoldStatuses.Held} AND created_at < now() - interval '30 minutes'
                """).ToListAsync(ct);
        }

        foreach (var launch in stuck.GroupBy(s => s.Launch))
        {
            metrics.SetStuck(launch.Key, launch.Count());
            var fresh = launch.Count(s => !_reported.ContainsKey(s.Hold));
            if (fresh > 0) logger.LogError("Launch {LaunchId} has {Count} holds newly held for more than 30 minutes: a checkout saga in order is stuck.", launch.Key, fresh);
        }
        foreach (var gone in _reported.Values.Distinct().Except(stuck.Select(s => s.Launch))) metrics.SetStuck(gone, 0);
        _reported = stuck.ToDictionary(s => s.Hold, s => s.Launch);
    }
}
