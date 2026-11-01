using Bloom.Identity.Codes;
using Bloom.Identity.Contracts;
using Bloom.Identity.Context;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.GoogleSignIn;
using Bloom.Identity.Sessions;
using Bloom.Identity.Validation;
using Bloom.Identity.V1;
using Bloom.Shared.Messaging;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Account = Bloom.Identity.Data.Account;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Account</c>: the signed-in caller's own account.</summary>
public sealed class AccountService(
    IdentityDbContext db, CodeService codes, Credentials credentials, PasswordPolicy passwordPolicy, PasswordHasher passwordHasher,
    SessionService sessions, OutboxWriter outbox, TimeProvider time, GoogleTokenVerifier google) : V1.Account.AccountBase
{
    public override async Task<GetMeResponse> GetMe(Empty request, ServerCallContext context)
    {
        var account = await LoadAsync(TrustedCaller.From(context), context);
        var me = new GetMeResponse
        {
            AccountId = account.Id.ToString("D"),
            Type = account.Type,
            Email = account.EmailDisplay ?? account.EmailNormalized,
            Name = account.Name ?? "",
            GoogleLinked = account.GoogleSub is not null,
            HasPassword = account.PasswordHash is not null,
        };
        if (account.Role is not null) me.Role = account.Role;
        return me;
    }

    public override async Task<Empty> ChangePassword(ChangePasswordRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireRecentAuth(time); // re-auth, new password, then the current one (plan I17)
        var next = passwordPolicy.Validate(request.NewPassword);
        var account = await LoadAsync(caller, context);
        await credentials.CheckAccountPasswordAsync(account, request.CurrentPassword);

        account.PasswordHash = passwordHasher.Hash(next);
        await using var tx = await db.Database.BeginTransactionAsync(context.CancellationToken);
        await sessions.RevokeAllAsync(account, "password_change"); // no new tokens: the caller signs in again (spec §7)
        await db.SaveChangesAsync(context.CancellationToken);
        await tx.CommitAsync(context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> ChangeEmail(ChangeEmailRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer().RequireRecentAuth(time);
        var (email, _) = Inputs.Email(request.NewEmail, "newEmail");
        if (await db.Accounts.AnyAsync(a => a.EmailNormalized == email, context.CancellationToken))
            throw IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account.");
        await codes.IssueAsync(email, CodePurposes.ChangeEmail, caller.UserId);
        await db.SaveChangesAsync(context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> ConfirmEmailChange(ConfirmEmailChangeRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer().RequireRecentAuth(time);
        var code = await codes.VerifyAsync(CodePurposes.ChangeEmail, request.Code, null, caller.UserId);
        var account = await LoadAsync(caller, context);

        account.EmailNormalized = code.TargetEmail;
        account.EmailDisplay = code.TargetEmail; // codes store only the normalized address (plan I33)
        outbox.PublishEvent(IdentityMessages.ContactChanged, new ContactChangedV1(account.Id, code.TargetEmail, account.Name ?? ""));
        try
        {
            await db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account.");
        }
        return new Empty();
    }

    public override async Task<Empty> LinkGoogle(LinkGoogleRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer(); // no re-auth: plan I18 does not list it
        var identity = await google.VerifyAsync(request.IdToken);
        var account = await LoadAsync(caller, context);

        // Replaces the caller's own sub (plan I19); a sub on another account is refused.
        account.GoogleSub = identity.Sub;
        try
        {
            await db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw IdentityErrors.Error(IdentityErrors.GoogleAlreadyLinked, "This Google account is linked to another account.");
        }
        return new Empty();
    }

    public override async Task<Empty> UnlinkGoogle(Empty request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer().RequireRecentAuth(time);
        var account = await LoadAsync(caller, context);
        if (account.PasswordHash is null)
            throw IdentityErrors.Error(IdentityErrors.PasswordRequired, "Set a password before unlinking Google.");

        account.GoogleSub = null;
        await db.SaveChangesAsync(context.CancellationToken);
        return new Empty();
    }

    private async Task<Account> LoadAsync(TrustedCaller caller, ServerCallContext context) =>
        await db.Accounts.SingleOrDefaultAsync(a => a.Id == caller.UserId, context.CancellationToken)
        ?? throw IdentityErrors.Error(IdentityErrors.NotFound, "No such account.");
}
