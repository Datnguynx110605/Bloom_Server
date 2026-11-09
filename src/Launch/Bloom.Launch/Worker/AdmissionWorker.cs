using Bloom.Launch.Data;
using Bloom.Launch.Observability;
using Bloom.Launch.WaitingRoom;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Bloom.Launch.Worker;

/// <param name="Result"><c>admitted</c>, <c>skipped_lock</c> or <c>none</c> (the <c>admission_ticks_total</c> label).</param>
/// <param name="Admitted">The admitted accounts, lowest score first.</param>
public sealed record AdmissionResult(string Result, IReadOnlyList<Guid> Admitted);

/// <summary>The admission tick of 01-launch §6.4, every second for each launch that is <c>preorder_open</c> and not closed (R14), and the room cleanup retry (R16).</summary>
public sealed class AdmissionWorker(
    IServiceScopeFactory scopes, IConnectionMultiplexer redis, RoomCleanup cleanup, StateCache cache, LaunchMetrics metrics, ILogger<AdmissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "The admission pass failed.");
            }
        }
    }

    /// <summary>One tick for every open launch, each in its own <c>try</c>; then the cleanup retry for launches closed within the last hour.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        List<Guid> open, closed;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LaunchDbContext>();
            open = await db.Launches.AsNoTracking().Where(l => l.Stage == LaunchStages.PreorderOpen && l.WaitingRoomClosedAt == null).Select(l => l.Id).ToListAsync(ct);
            closed = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM launches WHERE waiting_room_closed_at > now() - interval '1 hour'").ToListAsync(ct);
        }

        await EachAsync(open, "admission tick", id => TickLaunchAsync(id, ct));
        // Keys of a closed room that are back (a join on a stale cache, a failed delete) are deleted again; pass keys expire on their own.
        await EachAsync(closed, "room cleanup", async id =>
        {
            if (await redis.GetDatabase().KeyExistsAsync([RedisKeys.Queue(id), RedisKeys.Passes(id)]) > 0) await cleanup.RunAsync(id);
        });
    }

    private async Task EachAsync(List<Guid> launches, string what, Func<Guid, Task> work)
    {
        foreach (var id in launches)
        {
            try
            {
                await work(id);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "The {What} of launch {LaunchId} failed.", what, id);
            }
        }
    }

    /// <summary>§6.4 in its step order. A Redis failure throws; the transaction rolls back (nothing was written to Postgres before the script).</summary>
    public async Task<AdmissionResult> TickLaunchAsync(Guid launchId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LaunchDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // 1. One replica per launch.
        var key = launchId.ToString("D");
        if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtext('launch-admission:' || {key})) AS \"Value\"").SingleAsync(ct))
            return Done(launchId, "skipped_lock");

        var launch = await db.Launches.AsNoTracking().Where(l => l.Id == launchId).Select(l => new { l.Stage, l.WaitingRoomClosedAt, l.AdmissionRate }).SingleOrDefaultAsync(ct);
        if (launch is not { Stage: LaunchStages.PreorderOpen, WaitingRoomClosedAt: null }) return Done(launchId, "none");

        // 2. 3.
        var now = await db.NowAsync(ct);
        var available = await db.LaunchSkus.Where(s => s.LaunchId == launchId).SumAsync(s => (long)s.Allocation - s.Held - s.Sold, ct);
        var room = redis.GetDatabase();
        await room.SortedSetRemoveRangeByScoreAsync(RedisKeys.Passes(launchId), double.NegativeInfinity, now.ToUnixTimeMilliseconds());
        var outstanding = await room.SortedSetLengthAsync(RedisKeys.Passes(launchId));
        var queued = await room.SortedSetLengthAsync(RedisKeys.Queue(launchId));
        metrics.SetQueue(launchId, queued, outstanding);

        // 5. Sold out: closed for good when no hold is left to be released (LCH-R23); otherwise it waits for releases.
        if (available == 0)
        {
            var close = await TryCloseAsync(db, launchId, ct);
            await tx.CommitAsync(ct);
            if (close)
            {
                metrics.SetQueue(launchId, 0, 0);
                await cleanup.RunAsync(launchId);
                await cache.DropAsync(launchId);
            }
            return Done(launchId, "none");
        }

        // 4. 6.
        var n = (int)Math.Min(launch.AdmissionRate, available - outstanding);
        if (n <= 0)
        {
            await tx.CommitAsync(ct);
            return Done(launchId, "none");
        }

        // 7. 8.
        var passIds = Enumerable.Range(0, n).Select(_ => Guid.CreateVersion7()).ToList();
        var admitted = await AdmissionScript.RunAsync(room, launchId, passIds, now.AddSeconds(AdmissionScript.PassTtlSeconds).ToUnixTimeMilliseconds());
        await tx.CommitAsync(ct);
        metrics.Admitted(launchId, admitted.Count);
        metrics.SetQueue(launchId, queued - admitted.Count, outstanding + admitted.Count);
        return Done(launchId, admitted.Count > 0 ? "admitted" : "none", admitted);
    }

    /// <summary>
    /// The close of §6.4 step 5 as one statement that judges for itself: with READ COMMITTED a release can commit between the tick's reads
    /// and this statement, and a close is permanent (LCH-R23), so both conditions are checked again here. Zero rows: do not close.
    /// </summary>
    internal static async Task<bool> TryCloseAsync(LaunchDbContext db, Guid launchId, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE launches SET waiting_room_closed_at = now(), version = version + 1, updated_at = now()
            WHERE id = {launchId} AND waiting_room_closed_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM holds WHERE launch_id = {launchId} AND status = {HoldStatuses.Held})
              AND (SELECT COALESCE(SUM(allocation - held - sold), 0) FROM launch_skus WHERE launch_id = {launchId}) = 0
            """, ct) == 1;

    private AdmissionResult Done(Guid launchId, string result, IReadOnlyList<Guid>? admitted = null)
    {
        metrics.Tick(launchId, result);
        return new(result, admitted ?? []);
    }
}
