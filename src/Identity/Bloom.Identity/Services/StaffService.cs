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
using Account = Bloom.Identity.Data.Account;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Staff</c>.</summary>
public sealed class StaffService(
    IdentityDbContext db, AuthStore authStore, SessionService sessions, Credentials credentials, IdentityMetrics metrics, TimeProvider time,
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
        var existing = await db.Accounts.SingleOrDefaultAsync(a => a.EmailNormalized == invitation.EmailNormalized, ct);
        if (existing is { Type: AccountTypes.Staff }) // re-enrol only after ResetStaffMfa (spec §4, R43): an enrolled account's leftover link is dead, before any password check
            return existing.TotpSecretEnc is null
                ? await ReenrolAsync(request, invitation, existing, context)
                : throw IdentityErrors.Error(IdentityErrors.InvitationInvalid, "The invitation is not valid.");
        if (existing is not null) throw IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account.");

        var password = passwordPolicy.Validate(request.Password);
        var name = Inputs.Name(request.Name);
        var secret = ParseSecret(request.TotpSecret);

        var id = Guid.CreateVersion7(); // assigned up front for the replay mark
        await VerifyFirstCodeAsync(secret, request.TotpCode, id, now);

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
        // Claiming the invitation and creating the account commit together.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ClaimInvitationAsync(invitation, now, ct);
        db.Accounts.Add(account);
        var session = await sessions.StartAsync(account);
        await db.SaveOrThrowAsync(IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account."), ct);
        await tx.CommitAsync(ct);
        return RefreshCookie.Respond(context, session);
    }

    /// <summary>MFA re-enrolment: the current password and a first code on the new secret; name, password and role stay (plan I27).</summary>
    private async Task<TokenResponse> ReenrolAsync(AcceptStaffInvitationRequest request, StaffInvitation invitation, Account account, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var now = time.GetUtcNow();
        var secret = ParseSecret(request.TotpSecret);
        await credentials.CheckAccountPasswordAsync(account, request.CurrentPassword); // may save a rehash, so before anything is staged
        if (account.Status != AccountStatuses.Active) throw IdentityErrors.Error(IdentityErrors.InvalidCredentials, "Wrong email or password.");
        await VerifyFirstCodeAsync(secret, request.TotpCode, account.Id, now);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ClaimInvitationAsync(invitation, now, ct);
        account.TotpSecretEnc = totpBox.Seal(secret);
        var session = await sessions.StartAsync(account);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return RefreshCookie.Respond(context, session);
    }

    /// <summary>The first code proves the device. A wrong or replayed one leaves the invitation usable (plan I27).</summary>
    private async Task VerifyFirstCodeAsync(byte[] secret, string code, Guid accountId, DateTimeOffset now)
    {
        if (!Totp.Verify(secret, code, now, out var step) || !await authStore.TryMarkTotpUsedAsync(accountId, step))
            throw Invalid();
    }

    /// <summary>Of two concurrent accepts only one claims the invitation.</summary>
    private async Task ClaimInvitationAsync(StaffInvitation invitation, DateTimeOffset now, CancellationToken ct)
    {
        var claimed = await db.StaffInvitations.Where(i => i.Id == invitation.Id && i.AcceptedAt == null && i.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, now), ct);
        if (claimed == 0) throw IdentityErrors.Error(IdentityErrors.InvitationInvalid, "The invitation is not valid.");
    }

    private static byte[] ParseSecret(string base32) =>
        Base32.TryDecode(base32, out var secret) && secret.Length >= MinTotpSecretBytes
            ? secret
            : throw IdentityErrors.Validation("totp_secret", "The authenticator secret must be base32 and at least 160 bits.");

    private const int MinTotpSecretBytes = 20; // 160 bits (spec §7)

    private static RpcException Invalid() => IdentityErrors.Error(IdentityErrors.MfaInvalid, "The code is not valid.");
}
