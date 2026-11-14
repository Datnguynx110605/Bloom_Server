using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Observability;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Sessions;

public sealed record IssuedSession(string AccessToken, string RefreshToken, DateTimeOffset RefreshExpiresAt);

/// <summary>Refresh-token families (06-identity §6.3, §6.4).</summary>
public sealed class SessionService(
    IdentityDbContext db, TokenIssuer issuer, OutboxWriter outbox, IdentityMetrics metrics, IHttpContextAccessor http)
{
    private static readonly TimeSpan CustomerLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan StaffLifetime = TimeSpan.FromHours(12);

    /// <summary>Starts a family for an interactive sign-in. Adds rows only: the caller saves, with <paramref name="account"/> tracked.</summary>
    public async Task<IssuedSession> StartAsync(Account account)
    {
        var now = await db.Database.NowAsync();
        account.LastSignInAt = now;
        var lifetime = account.Type == AccountTypes.Customer ? CustomerLifetime : StaffLifetime;
        var familyId = Guid.CreateVersion7();
        return Add(account, familyId, now, now + lifetime, now);
    }

    /// <summary>Rotates the token inside its own transaction, the row locked by hash (I11, §7).</summary>
    /// <exception cref="Grpc.Core.RpcException"><c>REFRESH_INVALID</c></exception>
    public async Task<IssuedSession> RefreshAsync(string? rawToken)
    {
        IssuedSession? session = null;
        if (string.IsNullOrEmpty(rawToken)) metrics.Refresh("invalid");
        else session = await TryRotateAsync(Opaque.Sha256(rawToken));
        if (session is null) throw IdentityErrors.Error(IdentityErrors.RefreshInvalid, "Sign in again.");
        return session;
    }

    private async Task<IssuedSession?> TryRotateAsync(byte[] hash)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var now = await db.Database.NowAsync();
        // Lock order everywhere: the account row, then its tokens (R41). Otherwise a revoke-all could miss the token this rotation inserts.
        var accountId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.AccountId).SingleOrDefaultAsync();
        if (accountId is null)
        {
            metrics.Refresh("invalid");
            return null;
        }
        await LockAccountAsync(accountId.Value);
        // The loser of a race waited on the account row above, so this read sees the winner's committed "rotated" and takes the reuse path.
        var token = (await db.RefreshTokens.FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {hash} FOR UPDATE").ToListAsync()).SingleOrDefault();
        if (token is null || token.ExpiresAt <= now)
        {
            metrics.Refresh("invalid");
            return null;
        }

        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == token.AccountId);
        if (token.Status == RefreshStatuses.Revoked) // a deliberate revocation is not theft: no side effects (LLD-ADR-013)
        {
            metrics.Refresh("invalid");
            return null;
        }
        if (token.Status == RefreshStatuses.Rotated && account is not null) // reuse (ID-03)
        {
            await RevokeAllAsync(account, "refresh_reuse");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            metrics.Refresh("reuse");
            return null;
        }
        if (account is null || account.Status != AccountStatuses.Active)
        {
            token.Status = RefreshStatuses.Revoked;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            metrics.Refresh("invalid");
            return null;
        }

        token.Status = RefreshStatuses.Rotated;
        token.UsedAt = now;
        var expires = account.Type == AccountTypes.Customer ? now + CustomerLifetime : token.ExpiresAt; // staff: the family's absolute expiry
        var session = Add(account, token.FamilyId, token.AuthTime, expires, now);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        metrics.Refresh("rotated");
        return session;
    }

    private Task LockAccountAsync(Guid accountId) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM accounts WHERE id = {accountId} FOR UPDATE");

    /// <summary>Revokes every refresh token of the account in the caller's transaction; the caller saves.</summary>
    public async Task RevokeAllAsync(Account account, string trigger)
    {
        // Waits for a rotation in flight, so its new token is visible to the UPDATE (R41). The version is read under the lock: the caller's
        // copy may predate a concurrent revoke-all, and SaveChanges adds one to it (ID-2).
        account.Version = await db.Database.SqlQuery<long>($"SELECT version AS \"Value\" FROM accounts WHERE id = {account.Id} FOR UPDATE").SingleAsync();
        await db.RefreshTokens.Where(t => t.AccountId == account.Id && t.Status != RefreshStatuses.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, RefreshStatuses.Revoked));
        // clock_timestamp(), not the transaction's now(): this runs after the lock wait, so the stamp is later than the commit (and the access token's
        // iat, the rotation transaction's now()) of any rotation we waited on (R41). The gateway compares iat with it, so it must not be earlier.
        var validAfter = (await db.Database.SqlQuery<DateTimeOffset>(
            $"UPDATE accounts SET tokens_valid_after = clock_timestamp() WHERE id = {account.Id} RETURNING tokens_valid_after AS \"Value\"").ToListAsync()).Single();
        account.TokensValidAfter = validAfter;
        outbox.PublishEvent(IdentityMessages.TokensRevoked, new TokensRevokedV1(account.Id, validAfter.ToUnixTimeMilliseconds()));
        metrics.Revocation(trigger);
    }

    /// <summary>Revokes one family of the account in the caller's transaction. Already revoked is a no-op.</summary>
    /// <exception cref="Grpc.Core.RpcException"><c>NOT_FOUND</c> when the account has no such family.</exception>
    public async Task RevokeFamilyAsync(Guid accountId, Guid familyId)
    {
        await LockAccountAsync(accountId);
        var revoked = await db.RefreshTokens.Where(t => t.AccountId == accountId && t.FamilyId == familyId && t.Status != RefreshStatuses.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, RefreshStatuses.Revoked));
        if (revoked > 0) metrics.Revocation("sign_out");
        else if (!await db.RefreshTokens.AnyAsync(t => t.AccountId == accountId && t.FamilyId == familyId))
            throw IdentityErrors.Error(IdentityErrors.NotFound, "No such session.");
    }

    private IssuedSession Add(Account account, Guid familyId, DateTimeOffset authTime, DateTimeOffset expires, DateTimeOffset now)
    {
        var raw = Opaque.NewToken();
        var request = http.HttpContext?.Request;
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            FamilyId = familyId,
            AccountId = account.Id,
            TokenHash = Opaque.Sha256(raw),
            Status = RefreshStatuses.Active,
            AuthTime = authTime,
            ExpiresAt = expires,
            CreatedAt = now,
            UserAgent = request?.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });
        return new IssuedSession(issuer.CreateAccessToken(account, familyId, authTime, now), raw, expires);
    }
}
