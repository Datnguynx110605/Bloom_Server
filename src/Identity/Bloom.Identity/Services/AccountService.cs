using Bloom.Identity.Codes;
using Bloom.Identity.Contracts;
using Bloom.Identity.Context;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.GoogleSignIn;
using Bloom.Identity.Observability;
using Bloom.Identity.Orders;
using Bloom.Identity.Sessions;
using Bloom.Identity.Validation;
using Bloom.Identity.V1;
using Bloom.Shared.Concurrency;
using Bloom.Shared.Messaging;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Account = Bloom.Identity.Data.Account;
using AddressRow = Bloom.Identity.Data.Address;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Account</c>: the signed-in caller's own account.</summary>
public sealed class AccountService(
    IdentityDbContext db, CodeService codes, Credentials credentials, PasswordPolicy passwordPolicy, PasswordHasher passwordHasher,
    SessionService sessions, OutboxWriter outbox, TimeProvider time, GoogleTokenVerifier google, ConcurrentUpdates updates, OrderCheck orders, IdentityMetrics metrics) : V1.Account.AccountBase
{
    private const int MaxAddresses = 10;

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
        var (email, _) = Inputs.Email(request.NewEmail, "new_email");
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
        await db.SaveOrThrowAsync(IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account."), context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> LinkGoogle(LinkGoogleRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer(); // no re-auth: plan I18 does not list it
        var identity = await google.VerifyAsync(request.IdToken);
        var account = await LoadAsync(caller, context);

        // Replaces the caller's own sub (plan I19); a sub on another account is refused.
        account.GoogleSub = identity.Sub;
        await db.SaveOrThrowAsync(IdentityErrors.Error(IdentityErrors.GoogleAlreadyLinked, "This Google account is linked to another account."), context.CancellationToken);
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

    public override async Task<ListAddressesResponse> ListAddresses(Empty request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer();
        var rows = await db.Addresses.AsNoTracking().Where(a => a.AccountId == caller.UserId).OrderBy(a => a.Id).ToListAsync(context.CancellationToken);
        return new ListAddressesResponse { Addresses = { rows.Select(ToMessage) } };
    }

    public override async Task<V1.Address> UpsertAddress(V1.Address request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer();
        var recipient = Inputs.Text(request.Recipient, "recipient", 1);
        var line1 = Inputs.Text(request.Line1, "line1", 1);
        var city = Inputs.Text(request.City, "city", 1);
        var label = Inputs.Text(request.Label, "label", 0);
        var line2 = Inputs.Text(request.Line2, "line2", 0);
        var state = Inputs.State(request.State);
        var zip = Inputs.Zip(request.Zip);
        var phone = Inputs.Phone(request.Phone);
        var ct = context.CancellationToken;

        // One transaction: the default flag clears the other rows with ExecuteUpdate (plan I29).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var stored = new AddressRow
        {
            Id = Guid.CreateVersion7(), AccountId = caller.UserId, Label = label, Recipient = recipient, Line1 = line1, Line2 = line2,
            City = city, State = state, Zip = zip, Phone = phone, IsDefault = request.IsDefault,
        };
        if (request.Id.Length == 0)
        {
            // Lock the account row so two concurrent creates cannot pass the limit together.
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM accounts WHERE id = {caller.UserId} FOR UPDATE", ct);
            if (await db.Addresses.CountAsync(a => a.AccountId == caller.UserId, ct) >= MaxAddresses)
                throw IdentityErrors.Error(IdentityErrors.AddressLimitReached, $"At most {MaxAddresses} addresses.");
            if (request.IsDefault) await ClearDefaultsAsync(caller.UserId, stored.Id, ct);
            db.Addresses.Add(stored);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            if (!Guid.TryParse(request.Id, out var id)) throw IdentityErrors.Error(IdentityErrors.NotFound, "No such address.");
            stored.Id = id;
            var row = db.Addresses.Where(a => a.Id == stored.Id && a.AccountId == caller.UserId);
            if (request.IsDefault) await ClearDefaultsAsync(caller.UserId, stored.Id, ct);
            stored.Version = await updates.UpdateVersionedAsync("address", row, request.Version, b => b
                .SetProperty(a => a.Label, label).SetProperty(a => a.Recipient, recipient).SetProperty(a => a.Line1, line1)
                .SetProperty(a => a.Line2, line2).SetProperty(a => a.City, city).SetProperty(a => a.State, state)
                .SetProperty(a => a.Zip, zip).SetProperty(a => a.Phone, phone).SetProperty(a => a.IsDefault, request.IsDefault),
                IdentityErrors.NotFound, ct);
        }
        await tx.CommitAsync(ct);
        return ToMessage(stored);
    }

    public override async Task<Empty> DeleteAddress(DeleteAddressRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer();
        var deleted = Guid.TryParse(request.Id, out var id)
            && await db.Addresses.Where(a => a.Id == id && a.AccountId == caller.UserId).ExecuteDeleteAsync(context.CancellationToken) == 1;
        return deleted ? new Empty() : throw IdentityErrors.Error(IdentityErrors.NotFound, "No such address.");
    }

    public override async Task<Empty> DeleteAccount(Empty request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireCustomer().RequireRecentAuth(time);
        var account = await LoadAsync(caller, context);
        if (account.Status == AccountStatuses.Deleted) throw IdentityErrors.Error(IdentityErrors.NotFound, "No such account.");

        // §6.8 step 2, before any write: a refused or failed check leaves everything as it was.
        bool unsettled;
        try
        {
            unsettled = await orders.HasUnsettledOrdersAsync(account.Id);
        }
        catch (RpcException)
        {
            metrics.AccountDeletion("order_unavailable");
            throw;
        }
        if (unsettled)
        {
            metrics.AccountDeletion("unsettled_orders");
            throw IdentityErrors.Error(IdentityErrors.UnsettledOrders, "Orders are still open. Try again once they are settled.");
        }

        var ct = context.CancellationToken;
        var oldEmail = account.EmailNormalized;
        account.Status = AccountStatuses.Deleted;
        account.EmailNormalized = $"deleted+{account.Id:D}@invalid"; // frees the real email (§6.8)
        account.EmailDisplay = account.Name = account.PasswordHash = account.GoogleSub = null;
        await using var tx = await db.Database.BeginTransactionAsync(ct); // ExecuteDelete/Update mixed with tracked changes (plan I29)
        await db.Addresses.Where(a => a.AccountId == account.Id).ExecuteDeleteAsync(ct);
        await db.VerificationCodes.Where(c => c.AccountId == account.Id || c.TargetEmail == oldEmail).ExecuteDeleteAsync(ct); // plan I30
        await sessions.RevokeAllAsync(account, "account_deleted");
        outbox.PublishEvent(IdentityMessages.AccountDeleted, new AccountDeletedV1(account.Id));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.AccountDeletion("deleted");
        return new Empty();
    }

    // Losing the default changes the row, so its version moves too.
    private Task<int> ClearDefaultsAsync(Guid accountId, Guid except, CancellationToken ct) =>
        db.Addresses.Where(a => a.AccountId == accountId && a.Id != except && a.IsDefault)
            .ExecuteUpdateAsync(b => b.SetProperty(a => a.IsDefault, false).SetProperty(a => a.Version, a => a.Version + 1), ct);

    private static V1.Address ToMessage(AddressRow a) => new()
    {
        Id = a.Id.ToString("D"), Version = a.Version, Label = a.Label ?? "", Recipient = a.Recipient, Line1 = a.Line1, Line2 = a.Line2 ?? "",
        City = a.City, State = a.State, Zip = a.Zip, Phone = a.Phone ?? "", IsDefault = a.IsDefault,
    };

    private async Task<Account> LoadAsync(TrustedCaller caller, ServerCallContext context) =>
        await db.Accounts.SingleOrDefaultAsync(a => a.Id == caller.UserId, context.CancellationToken)
        ?? throw IdentityErrors.Error(IdentityErrors.NotFound, "No such account.");
}
