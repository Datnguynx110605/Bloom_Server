using Bloom.Notification.Data;
using Bloom.Notification.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Notification.Tests;

/// <summary>The daily purge (09-notification §6.6, spec §7; N-11), driven by <c>PurgeOnceAsync</c> (plan N11).</summary>
public class PurgeTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DailyPurge Purge(TestNotification t) => ActivatorUtilities.CreateInstance<DailyPurge>(t.Services);

    [Fact]
    public async Task N11_notifications_older_than_90_days_are_purged()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        var now = t.Clock.GetUtcNow();
        await using (var db = t.NewDb())
        {
            for (var i = 0; i < 2500; i++) db.Notifications.Add(Seed.Notification(t, account, now.AddDays(-91)));
            await db.SaveChangesAsync(Ct);
        }
        for (var i = 0; i < 3; i++) await Seed.NotificationAsync(t, account, now.AddDays(-89));

        await Purge(t).PurgeOnceAsync(Ct);

        await using var check = t.NewDb();
        Assert.Equal(3, await check.Notifications.CountAsync(n => n.AccountId == account, Ct));
        Assert.True(await check.Notifications.AllAsync(n => n.CreatedAt > now.AddDays(-90), Ct));
    }

    [Fact]
    public async Task Finished_email_jobs_older_than_30_days_are_purged()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var now = t.Clock.GetUtcNow();
        foreach (var status in new[] { EmailStatuses.Sent, EmailStatuses.Failed, EmailStatuses.Cancelled }) await Seed.JobAsync(t, status: status, createdAt: now.AddDays(-31));
        await Seed.JobAsync(t, createdAt: now.AddDays(-31));
        await Seed.JobAsync(t, status: EmailStatuses.Sent, createdAt: now.AddDays(-29));

        await Purge(t).PurgeOnceAsync(Ct);

        await using var db = t.NewDb();
        var left = await db.EmailJobs.AsNoTracking().Select(j => new { j.Status, Old = j.CreatedAt < now.AddDays(-30) }).ToListAsync(Ct);
        Assert.Equal(2, left.Count);
        Assert.Contains(left, j => j.Status == EmailStatuses.Pending && j.Old);
        Assert.Contains(left, j => j.Status == EmailStatuses.Sent && !j.Old);
    }

    [Fact]
    public async Task N6_notifications_purge_failure_still_purges_email_jobs()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var now = t.Clock.GetUtcNow();
        await Seed.NotificationAsync(t, Guid.NewGuid(), now.AddDays(-91));
        var old = await Seed.JobAsync(t, status: EmailStatuses.Sent, createdAt: now.AddDays(-31));
        await t.SqlAsync($"""
            CREATE FUNCTION refuse_delete() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'purge refused'; END $$
            """);
        await t.SqlAsync($"CREATE TRIGGER refuse BEFORE DELETE ON notifications FOR EACH ROW EXECUTE FUNCTION refuse_delete()");

        await Purge(t).PurgeOnceAsync(Ct);

        await using var db = t.NewDb();
        Assert.False(await db.EmailJobs.AnyAsync(j => j.Id == old, Ct));
        Assert.Equal(1, await db.Notifications.CountAsync(Ct));
        Assert.Single(t.Logs.Entries, e => e.StartsWith("[Error] Daily purge of notifications failed", StringComparison.Ordinal));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("Daily purge of email jobs failed"));
    }

    [Fact]
    public void Next_run_is_the_next_0300_utc()
    {
        var day = new DateTimeOffset(2030, 5, 6, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(day.AddHours(3), DailyPurge.NextRun(day.AddHours(2).AddMinutes(59)));
        Assert.Equal(day.AddDays(1).AddHours(3), DailyPurge.NextRun(day.AddHours(3)));
        Assert.Equal(day.AddDays(1).AddHours(3), DailyPurge.NextRun(day.AddHours(23)));
    }
}
