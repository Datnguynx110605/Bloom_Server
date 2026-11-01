using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Observability;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Sessions;

public sealed record IssuedSession(string AccessToken, string RefreshToken, DateTimeOffset RefreshExpiresAt);

/// <summary>Refresh-token families (06-identity §6.3, §6.4).</summary>
public sealed class SessionService(
    IdentityDbContext db, TokenIssuer issuer, OutboxWriter outbox, IdentityMetrics metrics, TimeProvider time, IHttpContextAccessor http)
{
    private static readonly TimeSpan CustomerLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan StaffLifetime = TimeSpan.FromHours(12);

    /// <summary>Starts a family for an interactive sign-in. Adds rows only: the caller saves, with <paramref name="account"/> tracked.</summary>
    public Task<IssuedSession> StartAsync(Account account)
    {
        var now = time.GetUtcNow();
        account.LastSignInAt = now;
        var lifetime = account.Type == AccountTypes.Customer ? CustomerLifetime : StaffLifetime;
        var familyId = Guid.CreateVersion7();
        return Task.FromResult(Add(account, familyId, now, now + lifetime));
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
        var now = time.GetUtcNow();
        // Materialised before the lock wait ends: the loser of a race re-reads the committed row, sees "rotated" and takes the reuse path.
        var token = (await db.RefreshTokens.FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {hash} FOR UPDATE").ToListAsync()).SingleOrDefault();
        if (token is null || token.ExpiresAt <= now)
        {
            metrics.Refresh("invalid");
            return null;
        }

        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == token.AccountId);
        if (token.Status != RefreshStatuses.Active && account is not null) // reuse (ID-03)
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
        var session = Add(account, token.FamilyId, token.AuthTime, expires);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        metrics.Refresh("rotated");
        return session;
    }

    /// <summary>Revokes every refresh token of the account in the caller's transaction; the caller saves.</summary>
    public async Task RevokeAllAsync(Account account, string trigger)
    {
        var now = time.GetUtcNow();
        await db.RefreshTokens.Where(t => t.AccountId == account.Id && t.Status != RefreshStatuses.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, RefreshStatuses.Revoked));
        account.TokensValidAfter = now;
        outbox.PublishEvent(IdentityMessages.TokensRevoked, new TokensRevokedV1(account.Id, now.ToUnixTimeMilliseconds()));
        metrics.Revocation(trigger);
    }

    /// <summary>Revokes one family of the account. Already revoked is a no-op.</summary>
    /// <exception cref="Grpc.Core.RpcException"><c>NOT_FOUND</c> when the account has no such family.</exception>
    public async Task RevokeFamilyAsync(Guid accountId, Guid familyId)
    {
        var revoked = await db.RefreshTokens.Where(t => t.AccountId == accountId && t.FamilyId == familyId && t.Status != RefreshStatuses.Revoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, RefreshStatuses.Revoked));
        if (revoked > 0) metrics.Revocation("sign_out");
        else if (!await db.RefreshTokens.AnyAsync(t => t.AccountId == accountId && t.FamilyId == familyId))
            throw IdentityErrors.Error(IdentityErrors.NotFound, "No such session.");
    }

    private IssuedSession Add(Account account, Guid familyId, DateTimeOffset authTime, DateTimeOffset expires)
    {
        var now = time.GetUtcNow();
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
        return new IssuedSession(issuer.CreateAccessToken(account, familyId, authTime), raw, expires);
    }
}
