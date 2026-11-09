using Bloom.Launch.Data;
using Bloom.Launch.Domain;
using Bloom.Launch.WaitingRoom;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Worker;

/// <summary>The stage scheduler of 01-launch §6.1, every 5 s: <c>announced → preorder_open</c> at <c>launch_time</c>, <c>preorder_open → released</c> at <c>release_date</c>. Database <c>now()</c> decides both.</summary>
public sealed class StageScheduler(IServiceScopeFactory scopes, StateCache cache, RoomCleanup cleanup, ILogger<StageScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "The stage scheduler pass failed.");
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await SweepAsync(release: false, ct);
        await SweepAsync(release: true, ct);
    }

    private async Task SweepAsync(bool release, CancellationToken ct)
    {
        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LaunchDbContext>();
            due = release
                ? await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM launches WHERE stage = {LaunchStages.PreorderOpen} AND release_date <= now()").ToListAsync(ct)
                : await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM launches WHERE stage = {LaunchStages.Announced} AND launch_time <= now()").ToListAsync(ct);
        }

        foreach (var id in due)
        {
            try
            {
                await TransitionAsync(id, release, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "The stage transition of launch {LaunchId} failed.", id);
            }
        }
    }

    /// <summary>One transaction: the conditional update (the single winner across replicas), then the event. Zero rows means another replica won.</summary>
    private async Task TransitionAsync(Guid id, bool release, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LaunchDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rows = release
            ? await db.Database.ExecuteSqlAsync(
                $"UPDATE launches SET stage = {LaunchStages.Released}, waiting_room_closed_at = COALESCE(waiting_room_closed_at, now()), version = version + 1, updated_at = now() WHERE id = {id} AND stage = {LaunchStages.PreorderOpen} AND release_date <= now()", ct)
            : await db.Database.ExecuteSqlAsync(
                $"UPDATE launches SET stage = {LaunchStages.PreorderOpen}, version = version + 1, updated_at = now() WHERE id = {id} AND stage = {LaunchStages.Announced} AND launch_time <= now()", ct);
        if (rows == 0) return;

        await StageEvents.WriteAsync(db, outbox, id, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await cache.DropAsync(id);
        if (release) await cleanup.RunAsync(id);
    }
}
