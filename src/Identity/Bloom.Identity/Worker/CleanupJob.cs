using Bloom.Identity.Data;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Worker;

/// <summary>Hourly: deletes verification codes, staff invitations and refresh tokens a day past <c>expires_at</c> (06-identity §3).</summary>
public sealed class CleanupJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<CleanupJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Identity cleanup failed; retrying next hour");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var cutoff = (await db.Database.NowAsync(ct)).AddDays(-1);
        await db.VerificationCodes.Where(x => x.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        await db.StaffInvitations.Where(x => x.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(x => x.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
