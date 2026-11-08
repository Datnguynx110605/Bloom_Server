using System.Text.Json;
using Bloom.Notification.Data;
using Bloom.Notification.Email;
using Bloom.Notification.Observability;
using Bloom.Notification.Templates;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Notification.Worker;

/// <summary>
/// Sends the pending <c>email_jobs</c> (09-notification §6.2, spec §7; plan N13, N17), every 2 s. A pass claims up to 50 due jobs by moving their <c>next_attempt_at</c> a minute ahead (a lease:
/// a crashed sender's jobs come due again). Before each send it renews that job's lease, and skips the job if another sender took it over meanwhile; the outcome is recorded with a statement guarded by
/// <c>status = 'pending'</c> (a job cancelled meanwhile stays cancelled) and by the lease it holds (a slow pass never overwrites a job another sender has taken).
/// Logs carry the job ID, template and reason only: never the exception (an SMTP one can echo the address), the address, the code or the body.
/// </summary>
public sealed class EmailSender(IServiceScopeFactory scopes, TimeProvider clock, NotificationOptions options, TemplateRenderer renderer, NotificationMetrics metrics, ILogger<EmailSender> logger) : BackgroundService
{
    // Retry delay after transient failure n (1-based); the 5th failure fails the job (§6.2 step 5).
    internal static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private const int MaxAttempts = 5;
    private const int Batch = 50;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan GaugeEvery = TimeSpan.FromSeconds(30);

    private sealed record StatusCount(string Status, long Count);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        var lastGauge = DateTimeOffset.MinValue;
        do
        {
            try
            {
                await RunOnceAsync(ct);
                if (clock.GetUtcNow() - lastGauge >= GaugeEvery)
                {
                    await RefreshGaugeAsync(ct);
                    lastGauge = clock.GetUtcNow();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError("Email sender pass failed ({Error}); retrying in 2 s", ex.GetType().Name); // never the exception (R-C)
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One pass; returns the number of jobs handled.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var provider = scope.ServiceProvider.GetRequiredService<IEmailProvider>();

        var now = clock.GetUtcNow();
        var lease = now + Lease;
        var jobs = await db.EmailJobs.FromSql($"""
            UPDATE email_jobs SET next_attempt_at = {lease}
            WHERE id IN (SELECT id FROM email_jobs WHERE status = {EmailStatuses.Pending} AND next_attempt_at <= {now} ORDER BY next_attempt_at LIMIT {Batch} FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);

        foreach (var claimed in jobs) await HandleAsync(db, provider, claimed, ct);
        return jobs.Count;
    }

    private async Task HandleAsync(NotificationDbContext db, IEmailProvider provider, EmailJobRow claimed, CancellationToken ct)
    {
        // Renew this job's lease for its own send; 0 rows: a second sender took it over, so it is theirs. The fence is the value the database returned, never one computed here (microsecond precision).
        var renewed = clock.GetUtcNow() + Lease;
        var job = (await db.EmailJobs.FromSql($"""
            UPDATE email_jobs SET next_attempt_at = {renewed} WHERE id = {claimed.Id} AND status = {EmailStatuses.Pending} AND next_attempt_at = {claimed.NextAttemptAt} RETURNING *
            """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (job is null) return;

        EmailMessage message;
        var failure = "render_failed"; // names the step in progress; never the exception, whose text may echo the code
        try
        {
            var vars = JsonSerializer.Deserialize<Dictionary<string, string>>(job.Data)!;
            if (vars.Remove("encryptedCode", out var encrypted))
            {
                failure = "decrypt_failed";
                vars["code"] = CodeCipher.Decrypt(options.CodeKey, encrypted);
                failure = "render_failed";
            }
            var (subject, html, text) = renderer.RenderEmail(job.Template, vars);
            message = new EmailMessage(job.ToEmail, subject, html, text);
        }
        catch (Exception)
        {
            await RecordFailureAsync(db, job, transient: false, failure, ct);
            return;
        }

        string? providerMessageId;
        var started = clock.GetTimestamp();
        try
        {
            providerMessageId = await provider.SendAsync(message, $"bloom:email:{job.Id:D}", ct);
        }
        catch (EmailSendException e)
        {
            await RecordFailureAsync(db, job, e.Transient, e.Reason, ct);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutting down: the lease runs out and another pass retries
        }
        catch (Exception) // for example a FormatException from a malformed address: one bad job never breaks the batch
        {
            await RecordFailureAsync(db, job, transient: true, "unexpected", ct);
            return;
        }
        finally
        {
            metrics.EmailSend(clock.GetElapsedTime(started).TotalSeconds);
        }

        var sentAt = clock.GetUtcNow();
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE email_jobs SET status = {EmailStatuses.Sent}, sent_at = {sentAt}, provider_message_id = {providerMessageId}, attempts = attempts + 1, data = data - 'encryptedCode'
            WHERE id = {job.Id} AND status = {EmailStatuses.Pending} AND next_attempt_at = {job.NextAttemptAt}
            """, ct);
        if (updated == 1) metrics.EventToEmail(job.Template, (sentAt - job.CreatedAt).TotalSeconds);
    }

    private async Task RecordFailureAsync(NotificationDbContext db, EmailJobRow job, bool transient, string reason, CancellationToken ct)
    {
        var attempts = job.Attempts + 1;
        if (transient && attempts < MaxAttempts)
        {
            var next = clock.GetUtcNow() + Backoff[attempts - 1];
            await db.Database.ExecuteSqlAsync($"""
                UPDATE email_jobs SET attempts = attempts + 1, next_attempt_at = {next}, last_error = {reason} WHERE id = {job.Id} AND status = {EmailStatuses.Pending} AND next_attempt_at = {job.NextAttemptAt}
                """, ct);
            return;
        }
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE email_jobs SET status = {EmailStatuses.Failed}, attempts = attempts + 1, last_error = {reason} WHERE id = {job.Id} AND status = {EmailStatuses.Pending} AND next_attempt_at = {job.NextAttemptAt}
            """, ct);
        if (updated == 1) logger.LogError("Email job {JobId} ({Template}) failed: {Reason}", job.Id, job.Template, reason);
    }

    /// <summary>Sets <c>email_jobs_by_status</c> from the table (plan N13); a status with no rows reads 0.</summary>
    internal async Task RefreshGaugeAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        var counts = await db.Database.SqlQuery<StatusCount>($"""SELECT status AS "Status", count(*) AS "Count" FROM email_jobs GROUP BY status""").ToListAsync(ct);
        metrics.SetEmailJobs(EmailStatuses.All.ToDictionary(s => s, s => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0));
    }
}
