using Bloom.Identity.Audit;
using Bloom.Identity.Contracts;
using Bloom.Identity.Context;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Identity.Errors;
using Bloom.Identity.Validation;
using Bloom.Identity.V1;
using Bloom.Shared.Messaging;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Services;

/// <summary>LLD 06-identity §3 component <c>Admin</c>; every method needs an admin (staff, role admin).</summary>
public sealed class AdminService(
    IdentityDbContext db, AuditWriter audit, OutboxWriter outbox, TimeProvider time, IdentityOptions options) : Admin.AdminBase
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
