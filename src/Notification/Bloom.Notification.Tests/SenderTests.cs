using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Notification.Data;
using Bloom.Notification.Worker;
using Bloom.Order.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Notification.Tests;

/// <summary>09-notification §6.2, §7, §8 through <c>EmailSender.RunOnceAsync</c> (plan N11, N13, N17, Review Focus 1).</summary>
public class SenderTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EmailSender Sender(TestNotification t) => ActivatorUtilities.CreateInstance<EmailSender>(t.Services);

    private static async Task<EmailJobRow> RowAsync(TestNotification t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.EmailJobs.AsNoTracking().SingleAsync(j => j.Id == id, Ct);
    }

    private static string Tampered(string sealedCode) => sealedCode[..10] + (sealedCode[10] == 'A' ? 'B' : 'A') + sealedCode[11..];

    [Fact]
    public async Task Pending_job_is_rendered_and_sent()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = Guid.NewGuid();
        var order = Guid.NewGuid();
        await Seed.ContactAsync(t, account, $"{account:N}@bloom.test", "Ada");
        await Seed.OrderRefAsync(t, order, account, "BL-1001");
        await Consume.Async(t, OrderMessages.Paid, new OrderPaidV1(order, "BL-1001", account, null, t.Clock.GetUtcNow(), 1000, []));
        var job = Assert.Single(await t.NewDb().EmailJobs.AsNoTracking().Where(j => j.AccountId == account).ToListAsync(Ct));

        var before = await t.DbNowAsync();
        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));
        var after = await t.DbNowAsync();

        var request = Assert.Single(t.Resend.Requests);
        Assert.Equal($"bloom:email:{job.Id:D}", request.IdempotencyKey);
        Assert.Equal("Order BL-1001 is confirmed", request.Body.GetProperty("subject").GetString());
        Assert.Contains("https://shop.test/orders/BL-1001", request.Body.GetProperty("html").GetString());
        Assert.Contains("https://shop.test/orders/BL-1001", request.Body.GetProperty("text").GetString());
        var row = await RowAsync(t, job.Id);
        Assert.Equal(EmailStatuses.Sent, row.Status);
        Assert.InRange(row.SentAt!.Value, before, after);
        Assert.Equal("re_1", row.ProviderMessageId);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, metrics.Count("event_to_email_seconds", ("type", "order.confirmed")));
        Assert.Equal(1, metrics.Count("email_send_seconds"));
    }

    [Fact]
    public async Task N03_code_email_is_sent_with_the_code_and_scrubbed()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var expiresAt = t.Clock.GetUtcNow().AddMinutes(10);
        await Consume.Async(t, IdentityMessages.VerificationCode, new VerificationCodeV1("Code@Bloom.test", "signup", TestNotification.EncryptCode("482913"), expiresAt));

        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));

        var request = Assert.Single(t.Resend.Requests);
        Assert.Contains("482913", request.Body.GetProperty("html").GetString());
        Assert.Contains("482913", request.Body.GetProperty("text").GetString());
        await using var db = t.NewDb();
        var row = await db.EmailJobs.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(EmailStatuses.Sent, row.Status);
        Assert.Equal(["expiresAt", "purpose"], JsonSerializer.Deserialize<Dictionary<string, string>>(row.Data)!.Keys.Order());
        Assert.DoesNotContain("482913", string.Join('|', row.Data, row.ToEmail, row.LastError, row.ProviderMessageId));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("482913"));
    }

    [Fact]
    public async Task Expired_code_is_still_sent()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var id = await Seed.JobAsync(t, "account.code", new() { ["purpose"] = "signup", ["encryptedCode"] = TestNotification.EncryptCode("111222"), ["expiresAt"] = "2020-01-01T00:00:00.0000000+00:00" });

        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));

        Assert.Equal(EmailStatuses.Sent, (await RowAsync(t, id)).Status);
        Assert.Contains("111222", Assert.Single(t.Resend.Requests).Body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Undecryptable_code_fails_without_sending()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var sealedCode = Tampered(TestNotification.EncryptCode("482913"));
        var id = await Seed.JobAsync(t, "account.code", new() { ["purpose"] = "signup", ["encryptedCode"] = sealedCode, ["expiresAt"] = "2030-01-01T00:00:00.0000000+00:00" }, to: "who@customer.test");

        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));

        var row = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Failed, row.Status);
        Assert.Equal("decrypt_failed", row.LastError);
        Assert.Equal(1, row.Attempts);
        Assert.Empty(t.Resend.Requests);
        Assert.Equal(1, t.ErrorLogs);
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("482913") || e.Contains(sealedCode) || e.Contains("who@customer.test"));
    }

    [Fact]
    public async Task N08_provider_500_five_times_fails_the_job()
    {
        await using var t = await TestNotification.StartAsync(containers);
        t.Resend.Fail(500, times: 5);
        var id = await Seed.JobAsync(t);
        TimeSpan[] delays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var before = await t.DbNowAsync();
            Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));
            var after = await t.DbNowAsync();
            var row = await RowAsync(t, id);
            Assert.Equal(EmailStatuses.Pending, row.Status);
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal("http_500", row.LastError);
            Assert.InRange(row.NextAttemptAt, before + delays[attempt - 1], after + delays[attempt - 1]);
            await t.SqlAsync($"UPDATE email_jobs SET next_attempt_at = now() + interval '1 hour' WHERE id = {id}"); // pinned, so "not due yet" needs no margin on the short delays
            Assert.Equal(0, await Sender(t).RunOnceAsync(Ct)); // not due yet
            await t.SqlAsync($"UPDATE email_jobs SET next_attempt_at = now() - interval '1 second' WHERE id = {id}"); // the delay has passed
        }
        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));

        var failed = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Failed, failed.Status);
        Assert.Equal(5, failed.Attempts);
        Assert.Equal("http_500", failed.LastError);
        Assert.Equal(1, t.ErrorLogs);

        await t.SqlAsync($"UPDATE email_jobs SET status = {EmailStatuses.Pending}, next_attempt_at = now() WHERE id = {id}"); // §8 Ops re-queue
        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));
        Assert.Equal(EmailStatuses.Sent, (await RowAsync(t, id)).Status);
    }

    /// <summary>A pending job that already failed twice, so its schedule step is 30 s (<c>Backoff[2]</c>).</summary>
    private static async Task<Guid> JobAtThirtySecondStepAsync(TestNotification t)
    {
        var id = await Seed.JobAsync(t);
        await t.SqlAsync($"UPDATE email_jobs SET attempts = 2 WHERE id = {id}");
        return id;
    }

    [Theory, InlineData("120", 120), InlineData("10", 30)]
    public async Task D39_429_with_retry_after_is_not_counted_and_waits_max(string retryAfter, int waitSeconds)
    {
        await using var t = await TestNotification.StartAsync(containers);
        t.Resend.Fail(429, retryAfter: retryAfter);
        var id = await JobAtThirtySecondStepAsync(t);

        var before = await t.DbNowAsync();
        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));
        var after = await t.DbNowAsync();

        var row = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Pending, row.Status);
        Assert.Equal(2, row.Attempts);
        Assert.Equal("http_429", row.LastError);
        Assert.InRange(row.NextAttemptAt, before.AddSeconds(waitSeconds), after.AddSeconds(waitSeconds));
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task D39_429_without_header_uses_schedule_step()
    {
        await using var t = await TestNotification.StartAsync(containers);
        t.Resend.Fail(429);
        var id = await JobAtThirtySecondStepAsync(t);

        var before = await t.DbNowAsync();
        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));
        var after = await t.DbNowAsync();

        var row = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Pending, row.Status);
        Assert.Equal(2, row.Attempts);
        Assert.InRange(row.NextAttemptAt, before.AddSeconds(30), after.AddSeconds(30));
    }

    [Fact]
    public async Task D39_429s_never_fail_the_job()
    {
        await using var t = await TestNotification.StartAsync(containers);
        t.Resend.Fail(429, times: 8);
        var id = await Seed.JobAsync(t);
        await t.SqlAsync($"UPDATE email_jobs SET attempts = 4 WHERE id = {id}"); // the next counted failure would end the job

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));
            await t.SqlAsync($"UPDATE email_jobs SET next_attempt_at = now() - interval '1 second' WHERE id = {id}"); // the wait has passed
        }

        var row = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Pending, row.Status);
        Assert.Equal(4, row.Attempts);
    }

    [Fact]
    public async Task N2_lease_lost_during_own_send()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var id = await Seed.JobAsync(t);
        var takenOver = (await t.DbNowAsync()).AddMinutes(2);
        // Another sender takes the job over while this pass is inside the provider call for it.
        t.Resend.Before = () => t.SqlAsync($"UPDATE email_jobs SET next_attempt_at = {takenOver} WHERE id = {id}");

        await Sender(t).RunOnceAsync(Ct);

        Assert.Single(t.Resend.Requests); // the send happened; recording its outcome is the new owner's job
        var row = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Pending, row.Status);
        Assert.Null(row.SentAt);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(takenOver, row.NextAttemptAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, metrics.Count("event_to_email_seconds", ("type", "order.confirmed")));
    }

    [Fact]
    public async Task N09_invalid_address_fails_at_once()
    {
        await using var t = await TestNotification.StartAsync(containers);
        t.Resend.Fail(422, "validation_error");
        var id = await Seed.JobAsync(t);

        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));

        var row = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Failed, row.Status);
        Assert.Equal("validation_error", row.LastError);
        Assert.Equal(1, row.Attempts);
        Assert.Single(t.Resend.Requests);
        Assert.Equal(1, t.ErrorLogs);
    }

    [Fact]
    public async Task Not_due_and_finished_jobs_are_skipped()
    {
        await using var t = await TestNotification.StartAsync(containers);
        await Seed.JobAsync(t, next: (await t.DbNowAsync()).AddHours(1));
        foreach (var status in new[] { EmailStatuses.Sent, EmailStatuses.Failed, EmailStatuses.Cancelled }) await Seed.JobAsync(t, status: status);

        Assert.Equal(0, await Sender(t).RunOnceAsync(Ct));

        Assert.Empty(t.Resend.Requests);
    }

    [Fact]
    public async Task Two_senders_send_each_job_once()
    {
        await using var t = await TestNotification.StartAsync(containers);
        t.Resend.Before = () => Task.Delay(50, Ct);
        var ids = new List<Guid>();
        for (var i = 0; i < 60; i++) ids.Add(await Seed.JobAsync(t));

        var handled = await Task.WhenAll(Sender(t).RunOnceAsync(Ct), Sender(t).RunOnceAsync(Ct));

        Assert.Equal(60, handled.Sum());
        var keys = t.Resend.Requests.Select(r => r.IdempotencyKey).ToList();
        Assert.Equal(60, keys.Count);
        Assert.Equal(60, keys.Distinct().Count());
        Assert.Equal(60, t.Resend.Delivered.Count);
        foreach (var id in ids) Assert.Equal(EmailStatuses.Sent, (await RowAsync(t, id)).Status);
    }

    [Fact]
    public async Task Claimed_job_waits_for_its_lease()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var id = await Seed.JobAsync(t, next: (await t.DbNowAsync()).AddMinutes(1)); // what a crashed sender leaves behind

        Assert.Equal(0, await Sender(t).RunOnceAsync(Ct));
        await t.SqlAsync($"UPDATE email_jobs SET next_attempt_at = next_attempt_at - interval '61 seconds' WHERE id = {id}"); // 61 s have passed since the lease was taken
        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));

        Assert.Equal(EmailStatuses.Sent, (await RowAsync(t, id)).Status);
    }

    [Fact]
    public async Task Due_check_and_retry_follow_the_database_clock_not_the_app_clock()
    {
        await using var t = await TestNotification.StartAsync(containers);
        t.Clock.Advance(TimeSpan.FromDays(2)); // the app clock is far ahead of the database
        t.Resend.Fail(500);
        var notYet = await Seed.JobAsync(t, next: (await t.DbNowAsync()).AddHours(1)); // an app-clock picker would find it due
        var due = await Seed.JobAsync(t);

        var before = await t.DbNowAsync();
        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));
        var after = await t.DbNowAsync();

        Assert.Equal(0, (await RowAsync(t, notYet)).Attempts);
        var row = await RowAsync(t, due);
        Assert.Equal(1, row.Attempts);
        Assert.InRange(row.NextAttemptAt, before + EmailSender.Backoff[0], after + EmailSender.Backoff[0]); // not two days from now
    }

    [Fact]
    public async Task Job_whose_lease_was_taken_over_is_skipped()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var first = await Seed.JobAsync(t);
        var second = await Seed.JobAsync(t);
        var takenOver = (await t.DbNowAsync()).AddMinutes(2);
        // While pass A sends the first job it holds, a second sender claims the other one (its next_attempt_at moves).
        t.Resend.Before = () => t.SqlAsync($"UPDATE email_jobs SET next_attempt_at = {takenOver} WHERE 'bloom:email:' || id::text <> {t.Resend.Requests[0].IdempotencyKey}");

        await Sender(t).RunOnceAsync(Ct);

        var request = Assert.Single(t.Resend.Requests); // the job A lost is not sent
        var sentId = Guid.Parse(request.IdempotencyKey!["bloom:email:".Length..]);
        var lost = await RowAsync(t, sentId == first ? second : first);
        Assert.Equal(EmailStatuses.Pending, lost.Status);
        Assert.Equal(0, lost.Attempts);
        Assert.Equal(takenOver, lost.NextAttemptAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(EmailStatuses.Sent, (await RowAsync(t, sentId)).Status);
    }

    [Fact]
    public async Task One_failure_does_not_block_the_others()
    {
        await using var t = await TestNotification.StartAsync(containers);
        t.Resend.Fail(500);
        var first = await Seed.JobAsync(t);
        var second = await Seed.JobAsync(t);

        Assert.Equal(2, await Sender(t).RunOnceAsync(Ct));

        var rows = new[] { await RowAsync(t, first), await RowAsync(t, second) };
        Assert.Equal([EmailStatuses.Pending, EmailStatuses.Sent], rows.Select(r => r.Status).Order()); // the first send got the 500 and waits for its retry
    }

    [Fact]
    public async Task Job_cancelled_during_the_send_stays_cancelled()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var id = await Seed.JobAsync(t);
        t.Resend.Before = () => t.SqlAsync($"UPDATE email_jobs SET status = {EmailStatuses.Cancelled} WHERE id = {id}");

        await Sender(t).RunOnceAsync(Ct);

        var row = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Cancelled, row.Status);
        Assert.Null(row.SentAt);
        Assert.Equal(0, t.ErrorLogs);
    }

    [Fact]
    public async Task Smtp_job_is_delivered_to_mailpit()
    {
        await using var t = await TestNotification.StartAsync(containers, o => o.Config["Notification:Email:Provider"] = "smtp");
        var to = $"{Guid.NewGuid():N}@customer.test";
        var id = await Seed.JobAsync(t, to: to);

        Assert.Equal(1, await Sender(t).RunOnceAsync(Ct));

        var row = await RowAsync(t, id);
        Assert.Equal(EmailStatuses.Sent, row.Status);
        Assert.Null(row.ProviderMessageId);
        var mail = await Mailpit.FindAsync(containers, to);
        Assert.NotNull(mail);
        Assert.Equal("Order BL-1001 is confirmed", mail.Subject);
        Assert.Empty(t.Resend.Requests);
    }

    [Fact]
    public async Task Malformed_address_fails_at_once_and_does_not_break_the_batch()
    {
        await using var t = await TestNotification.StartAsync(containers, o => o.Config["Notification:Email:Provider"] = "smtp");
        var bad = await Seed.JobAsync(t, to: "not an address");
        var good = await Seed.JobAsync(t);

        Assert.Equal(2, await Sender(t).RunOnceAsync(Ct));

        var row = await RowAsync(t, bad);
        Assert.Equal(EmailStatuses.Failed, row.Status);
        Assert.Equal("invalid_address", row.LastError);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, t.ErrorLogs);
        Assert.Equal(EmailStatuses.Sent, (await RowAsync(t, good)).Status);
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains("not an address"));
    }

    [Fact]
    public async Task Email_jobs_gauge_counts_by_status()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var later = (await t.DbNowAsync()).AddHours(1);
        await Seed.JobAsync(t, next: later);
        await Seed.JobAsync(t, next: later);
        await Seed.JobAsync(t, status: EmailStatuses.Sent);
        await Seed.JobAsync(t, status: EmailStatuses.Failed);

        await Sender(t).RefreshGaugeAsync(Ct);

        Assert.Equal(2, metrics.Latest("email_jobs_by_status", ("status", "pending")));
        Assert.Equal(1, metrics.Latest("email_jobs_by_status", ("status", "sent")));
        Assert.Equal(1, metrics.Latest("email_jobs_by_status", ("status", "failed")));
    }
}
