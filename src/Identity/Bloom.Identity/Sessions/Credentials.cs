using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Observability;
using Bloom.Identity.Redis;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Sessions;

/// <summary>Password checks with lockout (06-identity §6.1 steps 1-4, plan I12). Every refusal is the same <c>INVALID_CREDENTIALS</c> after one Argon2id verification (ID-05).</summary>
public sealed class Credentials(
    IdentityDbContext db, PasswordHasher hasher, PasswordPolicy policy, AuthStore store, IdentityMetrics metrics)
{
    /// <summary>The active account whose password this is. A malformed email is just an unknown one.</summary>
    /// <exception cref="Grpc.Core.RpcException"><c>INVALID_CREDENTIALS</c></exception>
    public async Task<Account> CheckPasswordAsync(string email, string password)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.EmailNormalized == email.Trim().ToLowerInvariant());
        if (!await VerifyAsync(account, password) || account!.Status != AccountStatuses.Active)
        {
            metrics.SignIn(account?.Type ?? "unknown", "password", "invalid");
            throw Invalid();
        }
        return account;
    }

    /// <summary>Steps 2-3 for a known account: block check, verification, counters. Does not look at the status.</summary>
    /// <exception cref="Grpc.Core.RpcException"><c>INVALID_CREDENTIALS</c></exception>
    public async Task CheckAccountPasswordAsync(Account account, string password)
    {
        if (!await VerifyAsync(account, password)) throw Invalid();
    }

    private async Task<bool> VerifyAsync(Account? account, string password)
    {
        var normalized = policy.Normalize(password);
        if (account is null || await store.IsSignInBlockedAsync(account.Id))
        {
            hasher.VerifyDummy(normalized); // same cost as a real check
            return false;
        }

        var (ok, needsRehash) = hasher.Verify(account.PasswordHash, normalized); // null hash (Google-only): dummy verification
        if (!ok)
        {
            await store.RecordSignInFailureAsync(account.Id);
            return false;
        }

        await store.ClearSignInFailuresAsync(account.Id);
        if (needsRehash)
        {
            account.PasswordHash = hasher.Hash(normalized);
            await db.SaveChangesAsync();
        }
        return true;
    }

    private static Grpc.Core.RpcException Invalid() => IdentityErrors.Error(IdentityErrors.InvalidCredentials, "Wrong email or password.");
}
