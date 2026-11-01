using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Observability;
using Bloom.Identity.Redis;
using Bloom.Identity.Sessions;
using Bloom.Identity.V1;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Staff</c>.</summary>
public sealed class StaffService(
    IdentityDbContext db, AuthStore authStore, SessionService sessions, IdentityMetrics metrics, TimeProvider time,
    [FromKeyedServices("totp")] SecretBox totpBox) : Staff.StaffBase
{
    public override async Task<TokenResponse> VerifyStaffMfa(VerifyStaffMfaRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var accountId = await authStore.TryUseChallengeAsync(request.MfaChallengeId) ?? throw Invalid();
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is not { Status: AccountStatuses.Active, TotpSecretEnc: not null } || await authStore.IsMfaBlockedAsync(account.Id))
            throw Invalid();

        // The replay mark is only set for a code that matches, and a replayed code counts as a failure.
        var ok = Totp.Verify(totpBox.Open(account.TotpSecretEnc), request.Code, time.GetUtcNow(), out var step)
            && await authStore.TryMarkTotpUsedAsync(account.Id, step);
        if (!ok)
        {
            await authStore.RecordMfaFailureAsync(account.Id);
            metrics.SignIn(account.Type, "mfa", "invalid");
            throw Invalid();
        }

        await authStore.ClearMfaFailuresAsync(account.Id);
        await authStore.DeleteChallengeAsync(request.MfaChallengeId);
        var session = await sessions.StartAsync(account);
        await db.SaveChangesAsync(ct);
        metrics.SignIn(account.Type, "mfa", "success");
        return RefreshCookie.Respond(context, session);
    }

    private static RpcException Invalid() => IdentityErrors.Error(IdentityErrors.MfaInvalid, "The code is not valid.");
}
