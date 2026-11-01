using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Observability;
using Bloom.Identity.Redis;
using Bloom.Identity.Sessions;
using Bloom.Identity.Validation;
using Bloom.Identity.V1;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Account = Bloom.Identity.Data.Account;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Staff</c>.</summary>
public sealed class StaffService(
    IdentityDbContext db, AuthStore authStore, SessionService sessions, IdentityMetrics metrics, TimeProvider time,
    PasswordPolicy passwordPolicy, PasswordHasher passwordHasher, [FromKeyedServices("totp")] SecretBox totpBox) : Staff.StaffBase
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

    public override async Task<TokenResponse> AcceptStaffInvitation(AcceptStaffInvitationRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var now = time.GetUtcNow();
        var hash = Opaque.Sha256(request.InviteToken);
        var invitation = await db.StaffInvitations.AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedAt == null && i.ExpiresAt > now, ct)
            ?? throw IdentityErrors.Error(IdentityErrors.InvitationInvalid, "The invitation is not valid.");
        if (await db.Accounts.AnyAsync(a => a.EmailNormalized == invitation.EmailNormalized, ct)) // Task 12: a staff account here is the MFA re-enrol branch
            throw IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account.");

        var password = passwordPolicy.Validate(request.Password);
        var name = Inputs.Name(request.Name);
        if (!Base32.TryDecode(request.TotpSecret, out var secret) || secret.Length < MinTotpSecretBytes)
            throw IdentityErrors.Validation("totp_secret", "The authenticator secret must be base32 and at least 160 bits.");

        // The first code proves the device. A wrong one leaves the invitation usable (plan I27); the account id is assigned up front for the replay mark.
        var id = Guid.CreateVersion7();
        if (!Totp.Verify(secret, request.TotpCode, now, out var step) || !await authStore.TryMarkTotpUsedAsync(id, step))
            throw Invalid();

        var account = new Account
        {
            Id = id,
            Type = AccountTypes.Staff,
            EmailNormalized = invitation.EmailNormalized,
            EmailDisplay = invitation.EmailNormalized, // plan I33
            PasswordHash = passwordHasher.Hash(password),
            Name = name,
            Role = invitation.Role,
            TotpSecretEnc = totpBox.Seal(secret),
            Status = AccountStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // Claiming the invitation and creating the account commit together; of two concurrent accepts only one claims it.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.StaffInvitations.Where(i => i.Id == invitation.Id && i.AcceptedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, now), ct);
        if (claimed == 0) throw IdentityErrors.Error(IdentityErrors.InvitationInvalid, "The invitation is not valid.");
        db.Accounts.Add(account);
        var session = await sessions.StartAsync(account);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account.");
        }
        await tx.CommitAsync(ct);
        return RefreshCookie.Respond(context, session);
    }

    private const int MinTotpSecretBytes = 20; // 160 bits (spec §7)

    private static RpcException Invalid() => IdentityErrors.Error(IdentityErrors.MfaInvalid, "The code is not valid.");
}
