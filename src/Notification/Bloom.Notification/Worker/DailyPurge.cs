using Bloom.Notification.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Notification.Worker;

/// <summary>
/// Daily at 03:00 UTC (09-notification §6.6, spec §7; N-11): deletes in-app notifications older than 90 days and finished (<c>sent</c>, <c>failed</c>, <c>cancelled</c>) email jobs older than
/// 30 days, by <c>created_at</c>, 1,000 rows per statement until a statement deletes fewer. The cut-offs are the database's <c>now()</c> minus the age (00-reliability §10); only the next-run wait uses the app clock.
/// </summary>
public sealed class DailyPurge(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DailyPurge> logger) : BackgroundService
{
    private const int Batch = 1000;

    /// <summary>The next 03:00 UTC strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextRun(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(3);
        return today > now ? today : today.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        for (var next = NextRun(clock.GetUtcNow()); ; next = NextRun(next))
        {
            var wait = next - clock.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, ct);
            try
            {
                await PurgeOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Daily purge failed; trying again at the next run");
            }
        }
    }

    public async Task PurgeOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        // Independent: one table's failure must not skip the other's purge (N-6).
        try
        {
            while (await db.Database.ExecuteSqlAsync($"""
                DELETE FROM notifications WHERE id IN (SELECT id FROM notifications WHERE created_at < now() - interval '90 days' LIMIT {Batch})
                """, ct) >= Batch) { }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Daily purge of notifications failed; trying again at the next run");
        }
        try
        {
            while (await db.Database.ExecuteSqlAsync($"""
                DELETE FROM email_jobs WHERE id IN (
                    SELECT id FROM email_jobs WHERE status IN ({EmailStatuses.Sent}, {EmailStatuses.Failed}, {EmailStatuses.Cancelled}) AND created_at < now() - interval '30 days' LIMIT {Batch})
                """, ct) >= Batch) { }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Daily purge of email jobs failed; trying again at the next run");
        }
    }
}
