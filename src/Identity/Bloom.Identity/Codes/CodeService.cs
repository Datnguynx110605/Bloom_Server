using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Observability;
using Bloom.Identity.Redis;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Identity.Codes;

/// <summary>Verification codes (06-identity §6.5, plan I14, I15).</summary>
public sealed class CodeService(
    IdentityDbContext db, AuthStore store, CodeHasher hasher, [FromKeyedServices("code")] SecretBox box,
    OutboxWriter outbox, IdentityMetrics metrics)
{
    private const int MaxAttempts = 5;

    private static TimeSpan Lifetime(string purpose) =>
        purpose == CodePurposes.ResetPassword ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(15);

    /// <summary>
    /// Applies the limits, invalidates the open codes of the same (email, purpose) and adds a new code and its
    /// <c>verification-code</c> message. The caller saves. <paramref name="targetEmail"/> is the normalized address.
    /// </summary>
    public async Task IssueAsync(string targetEmail, string purpose, Guid? accountId)
    {
        await store.CheckCodeLimitsAsync(targetEmail, purpose);

        var now = await db.Database.NowAsync();
        var previous = await db.VerificationCodes
            .Where(c => c.Purpose == purpose && c.ConsumedAt == null && (c.TargetEmail == targetEmail || (accountId != null && c.AccountId == accountId)))
            .ToListAsync();
        foreach (var old in previous) old.ConsumedAt = now;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expiresAt = now + Lifetime(purpose);
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = Guid.CreateVersion7(),
            Purpose = purpose,
            TargetEmail = targetEmail,
            AccountId = accountId,
            CodeHmac = hasher.Hash(Subject(targetEmail, accountId), purpose, code),
            ExpiresAt = expiresAt,
            CreatedAt = now,
        });
        outbox.PublishEvent(IdentityMessages.VerificationCode, new VerificationCodeV1(
            targetEmail, purpose, Base64Url.EncodeToString(box.Seal(Encoding.UTF8.GetBytes(code))), expiresAt));
        metrics.CodeSent(purpose);
    }

    /// <summary>
    /// Checks <paramref name="code"/> against the latest open code: of the account when <paramref name="accountId"/> is given
    /// (<c>change_email</c>), else of <paramref name="targetEmail"/>. A try is counted, and a correct code consumed, by conditional
    /// updates committed at once: concurrent guesses cannot exceed 5, and of two correct submissions only one wins (plan R10, R11).
    /// Call this before opening a transaction that could roll either back.
    /// </summary>
    /// <exception cref="Grpc.Core.RpcException"><c>CODE_INVALID</c></exception>
    public async Task<VerificationCode> VerifyAsync(string purpose, string code, string? targetEmail, Guid? accountId)
    {
        var now = await db.Database.NowAsync();
        var row = await db.VerificationCodes
            .Where(c => c.Purpose == purpose && c.ConsumedAt == null && (accountId != null ? c.AccountId == accountId : c.TargetEmail == targetEmail))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();
        if (row is null || row.ExpiresAt <= now
            || await db.VerificationCodes.Where(c => c.Id == row.Id && c.Attempts < MaxAttempts)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1)) == 0)
            throw Invalid(purpose);

        var expected = hasher.Hash(Subject(row.TargetEmail, row.AccountId), purpose, code);
        if (!CryptographicOperations.FixedTimeEquals(expected, row.CodeHmac)) throw Invalid(purpose);

        if (await db.VerificationCodes.Where(c => c.Id == row.Id && c.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now)) == 0)
            throw Invalid(purpose);
        return row;
    }

    private Grpc.Core.RpcException Invalid(string purpose)
    {
        metrics.CodeFailure(purpose);
        return IdentityErrors.Error(IdentityErrors.CodeInvalid, "The code is wrong or has expired.");
    }

    private static string Subject(string targetEmail, Guid? accountId) => accountId?.ToString("D") ?? targetEmail;
}
