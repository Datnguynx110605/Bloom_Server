using Bloom.Identity.Codes;
using Bloom.Identity.Contracts;
using Bloom.Identity.Context;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.GoogleSignIn;
using Bloom.Identity.Observability;
using Bloom.Identity.Redis;
using Bloom.Identity.Sessions;
using Bloom.Identity.Validation;
using Bloom.Identity.V1;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Account = Bloom.Identity.Data.Account;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Auth</c>.</summary>
public sealed class AuthService(
    SessionService sessions, IdentityDbContext db, CodeService codes, RegistrationTokens registrationTokens,
    PasswordPolicy passwordPolicy, PasswordHasher passwordHasher, OutboxWriter outbox,
    Credentials credentials, AuthStore authStore, IdentityMetrics metrics, GoogleTokenVerifier google) : Auth.AuthBase
{
    public override async Task<Empty> StartSignUp(StartSignUpRequest request, ServerCallContext context)
    {
        var (email, _) = Inputs.Email(request.Email);
        if (await db.Accounts.AnyAsync(a => a.EmailNormalized == email, context.CancellationToken))
            throw IdentityErrors.Error(IdentityErrors.EmailAlreadyRegistered, "This email already has an account.");
        await codes.IssueAsync(email, CodePurposes.SignUp, null);
        await db.SaveChangesAsync(context.CancellationToken);
        return new Empty();
    }

    public override async Task<VerifySignUpCodeResponse> VerifySignUpCode(VerifySignUpCodeRequest request, ServerCallContext context)
    {
        var (email, display) = Inputs.Email(request.Email);
        await codes.VerifyAsync(CodePurposes.SignUp, request.Code, email, null);
        await db.SaveChangesAsync(context.CancellationToken);
        return new VerifySignUpCodeResponse { RegistrationToken = registrationTokens.Create(display) };
    }

    public override async Task<TokenResponse> CompleteSignUp(CompleteSignUpRequest request, ServerCallContext context)
    {
        var (email, display) = Inputs.Email(registrationTokens.Read(request.RegistrationToken)); // the email comes from the token only (ID-T01c)
        var password = passwordPolicy.Validate(request.Password);
        var name = Inputs.Name(request.Name);

        var now = await db.Database.NowAsync(context.CancellationToken);
        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            Type = AccountTypes.Customer,
            EmailNormalized = email,
            EmailDisplay = display,
            PasswordHash = passwordHasher.Hash(password),
            Name = name,
            Status = AccountStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Accounts.Add(account);
        outbox.PublishEvent(IdentityMessages.CustomerRegistered, new CustomerRegisteredV1(account.Id, display, name));
        var session = await sessions.StartAsync(account);
        await db.SaveOrThrowAsync(IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account."), context.CancellationToken);
        return RefreshCookie.Respond(context, session);
    }

    public override async Task<SignInResponse> SignIn(SignInRequest request, ServerCallContext context)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var account = await credentials.CheckPasswordAsync(request.Email, request.Password);
            if (account.Type == AccountTypes.Staff)
            {
                if (account.TotpSecretEnc is null) // after ResetStaffMfa: only the re-enrol link gets them back in (spec §4)
                {
                    metrics.SignIn(account.Type, "password", "invalid");
                    throw IdentityErrors.Error(IdentityErrors.InvalidCredentials, "Wrong email or password.");
                }
                var challenge = await authStore.CreateChallengeAsync(account.Id);
                metrics.SignIn(account.Type, "password", "mfa_required");
                return new SignInResponse { MfaChallengeId = challenge };
            }

            var session = await sessions.StartAsync(account);
            await db.SaveChangesAsync(context.CancellationToken);
            metrics.SignIn(account.Type, "password", "success");
            return new SignInResponse { AccessToken = RefreshCookie.Respond(context, session).AccessToken };
        }
        finally
        {
            metrics.SignInDuration(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    public override async Task<TokenResponse> SignInWithGoogle(SignInWithGoogleRequest request, ServerCallContext context)
    {
        GoogleIdentity identity;
        try
        {
            identity = await google.VerifyAsync(request.IdToken);
        }
        catch (RpcException)
        {
            metrics.SignIn("unknown", "google", "invalid");
            throw;
        }

        var (email, display) = Inputs.Email(identity.Email);
        // 06-identity §6.2: the sub first, then the email.
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.GoogleSub == identity.Sub, context.CancellationToken)
            ?? await db.Accounts.SingleOrDefaultAsync(a => a.EmailNormalized == email, context.CancellationToken);
        if (account?.Type == AccountTypes.Staff)
        {
            metrics.SignIn(AccountTypes.Staff, "google", "not_allowed");
            throw IdentityErrors.Error(IdentityErrors.GoogleNotAllowed, "Staff sign in with their password.");
        }
        if (account is not null && account.Status != AccountStatuses.Active)
        {
            metrics.SignIn(account.Type, "google", "invalid");
            throw IdentityErrors.Error(IdentityErrors.InvalidCredentials, "The Google sign-in is not valid.");
        }
        // §6.2: a linked Google identity is never replaced (found by sub, GoogleSub equals it; so a difference means found by email).
        if (account?.GoogleSub is { } linked && linked != identity.Sub)
            throw IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account.");

        if (account is null)
        {
            var now = await db.Database.NowAsync(context.CancellationToken);
            // I19: the name claim, else the email's local part, at most 100 scalars.
            var raw = string.IsNullOrWhiteSpace(identity.Name) ? display[..display.LastIndexOf('@')] : identity.Name.Trim();
            account = new Account
            {
                Id = Guid.CreateVersion7(),
                Type = AccountTypes.Customer,
                EmailNormalized = email,
                EmailDisplay = display,
                GoogleSub = identity.Sub,
                Name = string.Concat(raw.EnumerateRunes().Take(100)),
                Status = AccountStatuses.Active,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Accounts.Add(account);
            outbox.PublishEvent(IdentityMessages.CustomerRegistered, new CustomerRegisteredV1(account.Id, display, account.Name));
        }
        else account.GoogleSub = identity.Sub;

        var session = await sessions.StartAsync(account);
        await db.SaveOrThrowAsync(IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account."), context.CancellationToken); // a concurrent first sign-in won
        metrics.SignIn(AccountTypes.Customer, "google", "success");
        return RefreshCookie.Respond(context, session);
    }

    public override async Task<Empty> RequestPasswordReset(RequestPasswordResetRequest request, ServerCallContext context)
    {
        var (email, _) = Inputs.Email(request.Email);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.EmailNormalized == email && a.Status == AccountStatuses.Active, context.CancellationToken);
        // The limits apply to unknown and inactive emails too, so the answer never reveals an account (ID-05, plan I14).
        if (account is null) await authStore.CheckCodeLimitsAsync(email, CodePurposes.ResetPassword);
        else await codes.IssueAsync(email, CodePurposes.ResetPassword, account.Id);
        await db.SaveChangesAsync(context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> ResetPassword(ResetPasswordRequest request, ServerCallContext context)
    {
        var (email, _) = Inputs.Email(request.Email);
        var password = passwordPolicy.Validate(request.NewPassword); // before the code, so a weak password spends no attempt (plan I16)
        await codes.VerifyAsync(CodePurposes.ResetPassword, request.Code, email, null);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.EmailNormalized == email && a.Status == AccountStatuses.Active, context.CancellationToken)
            ?? throw IdentityErrors.Error(IdentityErrors.CodeInvalid, "The code is wrong or has expired.");

        account.PasswordHash = passwordHasher.Hash(password);
        await using var tx = await db.Database.BeginTransactionAsync(context.CancellationToken);
        await sessions.RevokeAllAsync(account, "password_reset");
        await db.SaveChangesAsync(context.CancellationToken);
        await tx.CommitAsync(context.CancellationToken);
        return new Empty();
    }

    public override async Task<TokenResponse> Refresh(Empty request, ServerCallContext context)
    {
        var http = context.GetHttpContext();
        http.Response.Headers.CacheControl = "no-store"; // on errors too (I11)
        return RefreshCookie.Respond(context, await sessions.RefreshAsync(RefreshCookie.Read(http)));
    }

    public override async Task<Empty> SignOut(SignOutRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context);
        if (!Guid.TryParse(request.Sid, out var familyId))
            throw IdentityErrors.Error(IdentityErrors.NotFound, "No such session.");
        await using var tx = await db.Database.BeginTransactionAsync(context.CancellationToken);
        await sessions.RevokeFamilyAsync(caller.UserId, familyId);
        await tx.CommitAsync(context.CancellationToken);
        RefreshCookie.Clear(context.GetHttpContext());
        return new Empty();
    }

    public override async Task<Empty> SignOutEverywhere(Empty request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context);
        await using var tx = await db.Database.BeginTransactionAsync(context.CancellationToken);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == caller.UserId, context.CancellationToken)
            ?? throw IdentityErrors.Error(IdentityErrors.NotFound, "No such account.");
        await sessions.RevokeAllAsync(account, "sign_out_everywhere");
        await db.SaveChangesAsync(context.CancellationToken);
        await tx.CommitAsync(context.CancellationToken);
        return new Empty();
    }
}
