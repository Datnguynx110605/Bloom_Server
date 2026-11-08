using Bloom.Notification.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Notification.Worker;

/// <summary>
/// Daily at 03:00 UTC (09-notification §6.6, spec §7; N-11): deletes in-app notifications older than 90 days and finished (<c>sent</c>, <c>failed</c>, <c>cancelled</c>) email jobs older than
/// 30 days, by <c>created_at</c>, 1,000 rows per statement until a statement deletes fewer. The cut-offs come from the app clock as parameters, never <c>now()</c>.
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
        var now = clock.GetUtcNow();
        var notificationCutoff = now.AddDays(-90);
        var jobCutoff = now.AddDays(-30);

        while (await db.Database.ExecuteSqlAsync($"""
            DELETE FROM notifications WHERE id IN (SELECT id FROM notifications WHERE created_at < {notificationCutoff} LIMIT {Batch})
            """, ct) >= Batch) { }
        while (await db.Database.ExecuteSqlAsync($"""
            DELETE FROM email_jobs WHERE id IN (
                SELECT id FROM email_jobs WHERE status IN ({EmailStatuses.Sent}, {EmailStatuses.Failed}, {EmailStatuses.Cancelled}) AND created_at < {jobCutoff} LIMIT {Batch})
            """, ct) >= Batch) { }
    }
}
