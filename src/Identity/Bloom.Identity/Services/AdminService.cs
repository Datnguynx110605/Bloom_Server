using Bloom.Identity.Audit;
using Bloom.Identity.Contracts;
using Bloom.Identity.Context;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Sessions;
using Bloom.Identity.Validation;
using Bloom.Identity.V1;
using Bloom.Shared.Messaging;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Account = Bloom.Identity.Data.Account;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Admin</c>; every method needs an admin (staff, role admin).</summary>
public sealed class AdminService(
    IdentityDbContext db, AuditWriter audit, OutboxWriter outbox, SessionService sessions, TimeProvider time, IdentityOptions options,
    ILogger<AdminService> logger) : Admin.AdminBase
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromHours(72);

    public override async Task<Empty> InviteStaff(InviteStaffRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireAdmin();
        var (email, _) = Inputs.Email(request.Email);
        if (!StaffRoles.All.Contains(request.Role)) throw IdentityErrors.Validation("role", "Choose one of the staff roles.");
        if (await db.Accounts.AnyAsync(a => a.EmailNormalized == email, context.CancellationToken))
            throw IdentityErrors.Error(IdentityErrors.EmailTaken, "This email already has an account.");

        AddInvitation(email, request.Role, caller.UserId);
        audit.Add(caller, "identity.staff-invited", email, new { role = request.Role }); // no account yet: the target is the email (plan I26)
        await db.SaveChangesAsync(context.CancellationToken);
        return new Empty();
    }

    public override async Task<ListStaffResponse> ListStaff(Empty request, ServerCallContext context)
    {
        TrustedCaller.From(context).RequireAdmin();
        var staff = await db.Accounts.AsNoTracking().Where(a => a.Type == AccountTypes.Staff).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToListAsync(context.CancellationToken);
        return new ListStaffResponse
        {
            Staff =
            {
                staff.Select(a => new StaffMember
                {
                    AccountId = a.Id.ToString(),
                    Email = a.EmailNormalized,
                    Name = a.Name ?? "",
                    Role = a.Role ?? "",
                    Status = a.Status,
                    MfaEnrolled = a.TotpSecretEnc is not null,
                    CreatedAt = Timestamp.FromDateTimeOffset(a.CreatedAt),
                    LastSignInAt = a.LastSignInAt is { } last ? Timestamp.FromDateTimeOffset(last) : null,
                }),
            },
        };
    }

    public override async Task<Empty> ChangeStaffRole(ChangeStaffRoleRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireAdmin();
        if (!StaffRoles.All.Contains(request.Role)) throw IdentityErrors.Validation("role", "Choose one of the staff roles.");
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var target = await LockStaffAsync(request.Id, guardLastAdmin: request.Role != StaffRoles.Admin, "change the role of", ct);
        if (target.Role == request.Role) return new Empty();

        var before = target.Role;
        target.Role = request.Role;
        await sessions.RevokeAllAsync(target, "staff_role_changed"); // new tokens must carry the new role
        audit.Add(caller, "identity.staff-role-changed", target.Id.ToString(), new { before, after = request.Role });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new Empty();
    }

    public override async Task<Empty> DeactivateStaff(StaffMemberRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireAdmin();
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var target = await LockStaffAsync(request.Id, guardLastAdmin: true, "deactivate", ct);
        if (target.Status == AccountStatuses.Deactivated) return new Empty();

        target.Status = AccountStatuses.Deactivated;
        await sessions.RevokeAllAsync(target, "staff_deactivated");
        audit.Add(caller, "identity.staff-deactivated", target.Id.ToString(), new { before = AccountStatuses.Active, after = AccountStatuses.Deactivated });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new Empty();
    }

    public override async Task<Empty> ReactivateStaff(StaffMemberRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireAdmin();
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var target = await LockStaffAsync(request.Id, guardLastAdmin: false, "reactivate", ct);
        if (target.Status == AccountStatuses.Active) return new Empty();

        target.Status = AccountStatuses.Active;
        audit.Add(caller, "identity.staff-reactivated", target.Id.ToString(), new { before = AccountStatuses.Deactivated, after = AccountStatuses.Active });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new Empty();
    }

    public override async Task<Empty> ResetStaffMfa(StaffMemberRequest request, ServerCallContext context)
    {
        var caller = TrustedCaller.From(context).RequireAdmin();
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var target = await LockStaffAsync(request.Id, guardLastAdmin: false, "reset the MFA of", ct);

        target.TotpSecretEnc = null;
        await sessions.RevokeAllAsync(target, "staff_mfa_reset");
        AddInvitation(target.EmailNormalized, target.Role!, caller.UserId);
        audit.Add(caller, "identity.staff-mfa-reset", target.Id.ToString(), new { });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new Empty();
    }

    /// <summary>
    /// Inside the caller's transaction: locks the target staff row and, when <paramref name="guardLastAdmin"/>, every active admin row first
    /// (in id order, so two admins acting on each other cannot deadlock) and refuses to take the only one away (§5.3, plan I25).
    /// </summary>
    /// <exception cref="Grpc.Core.RpcException"><c>NOT_FOUND</c> unless the id is a staff account; <c>LAST_ADMIN</c>.</exception>
    private async Task<Account> LockStaffAsync(string rawId, bool guardLastAdmin, string attempt, CancellationToken ct)
    {
        var notFound = IdentityErrors.Error(IdentityErrors.NotFound, "No such staff member.");
        if (!Guid.TryParse(rawId, out var id)) throw notFound;
        var admins = guardLastAdmin
            ? await db.Accounts.FromSql($"SELECT * FROM accounts WHERE type = 'staff' AND role = 'admin' AND status = 'active' ORDER BY id FOR UPDATE").ToListAsync(ct)
            : [];
        var target = (await db.Accounts.FromSql($"SELECT * FROM accounts WHERE id = {id} AND type = 'staff' FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
            ?? throw notFound;
        if (admins.Count == 1 && admins[0].Id == id)
        {
            logger.LogWarning("LAST_ADMIN: refused to {Attempt} the only active admin {AccountId}", attempt, id);
            throw IdentityErrors.Error(IdentityErrors.LastAdmin, "This is the only active admin.");
        }
        return target;
    }

    /// <summary>Adds a 72 h invitation and its <c>staff-invited</c> message; the caller saves. The raw token leaves only inside the message's URL.</summary>
    private void AddInvitation(string emailNormalized, string role, Guid invitedBy)
    {
        var token = Opaque.NewToken();
        db.StaffInvitations.Add(new StaffInvitation
        {
            Id = Guid.CreateVersion7(),
            EmailNormalized = emailNormalized,
            Role = role,
            TokenHash = Opaque.Sha256(token),
            InvitedBy = invitedBy,
            ExpiresAt = time.GetUtcNow() + InvitationLifetime,
        });
        outbox.PublishEvent(IdentityMessages.StaffInvited, new StaffInvitedV1(emailNormalized, role, $"{options.InviteBaseUrl}?token={token}"));
    }
}
