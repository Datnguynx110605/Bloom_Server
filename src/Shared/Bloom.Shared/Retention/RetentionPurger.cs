using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bloom.Shared.Retention;

/// <summary>Daily purge of expired rows in batches of 1,000 (LLD 00-reliability §13).</summary>
internal sealed class RetentionPurger(IServiceScopeFactory scopes, ILogger<RetentionPurger> logger) : BackgroundService
{
    private const int BatchSize = 1000;

    // §13 rules verbatim; pending outbox rows never match.
    private static readonly string[] Deletes =
    [
        $"DELETE FROM outbox_messages WHERE id IN (SELECT id FROM outbox_messages WHERE status = 'sent' AND sent_at < now() - interval '7 days' LIMIT {BatchSize})",
        $"DELETE FROM inbox_messages WHERE (consumer, message_id) IN (SELECT consumer, message_id FROM inbox_messages WHERE processed_at < now() - interval '14 days' LIMIT {BatchSize})",
        $"DELETE FROM idempotency_keys WHERE (user_id, key) IN (SELECT user_id, key FROM idempotency_keys WHERE created_at < now() - interval '24 hours' LIMIT {BatchSize})",
    ];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try { await PurgeOnceAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogError(ex, "Retention purge failed"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task PurgeOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        foreach (var sql in Deletes)
            while (await db.Database.ExecuteSqlRawAsync(sql, ct) == BatchSize) { }
    }
}
