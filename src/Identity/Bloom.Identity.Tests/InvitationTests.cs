using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Bloom.Identity.Tests;

public class InvitationTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string ToBase32(byte[] bytes)
    {
        var chars = new List<char>();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars.Add(Base32Alphabet[(buffer >> bits) & 31]);
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) chars.Add(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return new string([.. chars]);
    }

    private static TestCaller AdminCaller(TestIdentity t, Account admin) => TestCaller.Staff(admin.Id, StaffRoles.Admin, t.Clock.GetUtcNow());

    private static Task<HttpResponseMessage> InviteAsync(TestIdentity t, TestCaller? caller, string email, string role) =>
        t.SendAsync(HttpMethod.Post, "/api/admin/staff/invitations", new { email, role }, caller);

    private static Task<HttpResponseMessage> AcceptAsync(TestIdentity t, string token, string totpSecret, string totpCode, string name = "New Staff", string password = Seed.Password) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/staff/invitations/accept", new { inviteToken = token, name, password, totpSecret, totpCode });

    private static string CodeAt(TestIdentity t, byte[] secret, long stepOffset = 0) =>
        Totp.Compute(secret, t.Clock.GetUtcNow().ToUnixTimeSeconds() / 30 + stepOffset);

    /// <summary>Invites as a fresh admin and returns the raw token from the published invite URL.</summary>
    private static async Task<(string Email, string Token)> InvitedAsync(TestIdentity t, string role = StaffRoles.Warehouse, string? email = null)
    {
        email ??= $"{Guid.NewGuid():N}@bloom.example";
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await InviteAsync(t, AdminCaller(t, admin), email, role)).StatusCode);
        var invite = (await Outbox.EnvelopesAsync(t, IdentityMessages.StaffInvited)).Last().GetPayload<StaffInvitedV1>();
        return (invite.Email, invite.InviteUrl.Split("?token=")[1]);
    }

    [Fact]
    public async Task Invite_writes_invitation_message_and_audit()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);

        var response = await InviteAsync(t, AdminCaller(t, admin), " New.Hire@Bloom.Example ", StaffRoles.CatalogManager);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.StaffInvited));
        var message = envelope.GetPayload<StaffInvitedV1>();
        Assert.Equal("new.hire@bloom.example", message.Email);
        Assert.Equal(StaffRoles.CatalogManager, message.Role);
        Assert.StartsWith("https://bloom.example/staff/invitation?token=", message.InviteUrl);
        var raw = message.InviteUrl.Split("?token=")[1];

        await using var db = t.NewDb();
        var invitation = await db.StaffInvitations.SingleAsync(Ct);
        Assert.Equal(Opaque.Sha256(raw), invitation.TokenHash);
        Assert.Equal("new.hire@bloom.example", invitation.EmailNormalized);
        Assert.Equal(StaffRoles.CatalogManager, invitation.Role);
        Assert.Equal(admin.Id, invitation.InvitedBy);
        Assert.Equal(t.Clock.GetUtcNow().AddHours(72), invitation.ExpiresAt, TimeSpan.FromMilliseconds(1));
        Assert.Null(invitation.AcceptedAt);

        var audit = await db.AuditEntries.SingleAsync(Ct);
        Assert.Equal(admin.Id, audit.ActorId);
        Assert.Equal(StaffRoles.Admin, audit.ActorRole);
        Assert.Equal("identity", audit.Service);
        Assert.Equal("identity.staff-invited", audit.Action);
        Assert.Equal("staff_account", audit.TargetType);
        Assert.Equal("new.hire@bloom.example", audit.TargetId);
        using (var details = JsonDocument.Parse(audit.Details))
            Assert.Equal(StaffRoles.CatalogManager, details.RootElement.GetProperty("role").GetString());
        Assert.Equal(1, metrics.Sum("audit_entries_total", ("service", "identity")));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Message.Contains(raw));
    }

    [Fact]
    public async Task Invite_for_existing_email_is_EMAIL_TAKEN()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        await Seed.CustomerAsync(t, "ann@example.com");
        var (staff, _) = await Seed.StaffAsync(t);

        await (await InviteAsync(t, AdminCaller(t, admin), "Ann@Example.COM", StaffRoles.Support)).ExpectErrorAsync(409, "EMAIL_TAKEN");
        await (await InviteAsync(t, AdminCaller(t, admin), staff.EmailNormalized.ToUpperInvariant(), StaffRoles.Support)).ExpectErrorAsync(409, "EMAIL_TAKEN");

        await using var db = t.NewDb();
        Assert.Empty(await db.StaffInvitations.ToListAsync(Ct));
        Assert.Empty(await db.AuditEntries.ToListAsync(Ct));
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.StaffInvited));
    }

    [Fact]
    public async Task Invite_input_and_caller_checks()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (support, _) = await Seed.StaffAsync(t, StaffRoles.Support);
        var customer = await Seed.CustomerAsync(t);

        var role = await (await InviteAsync(t, AdminCaller(t, admin), "a@bloom.example", "owner")).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("role", role.GetProperty("metadata").GetProperty("field").GetString());
        var email = await (await InviteAsync(t, AdminCaller(t, admin), "not-an-email", StaffRoles.Support)).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("email", email.GetProperty("metadata").GetProperty("field").GetString());

        await (await InviteAsync(t, TestCaller.Staff(support.Id, StaffRoles.Support, t.Clock.GetUtcNow()), "a@bloom.example", StaffRoles.Support))
            .ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        await (await InviteAsync(t, TestCaller.Customer(customer.Id, t.Clock.GetUtcNow()), "a@bloom.example", StaffRoles.Support))
            .ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");

        await using var db = t.NewDb();
        Assert.Empty(await db.StaffInvitations.ToListAsync(Ct));
    }

    [Fact]
    public async Task Accept_creates_staff_and_signs_in()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (email, token) = await InvitedAsync(t, StaffRoles.Warehouse);
        var secret = RandomNumberGenerator.GetBytes(20);

        var response = await AcceptAsync(t, token, ToBase32(secret), CodeAt(t, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(response.RefreshCookie()));
        var jwt = new JsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!);
        Assert.Equal(StaffRoles.Warehouse, jwt.GetClaim("role").Value);
        Assert.Equal("staff", jwt.GetClaim("typ").Value);

        await using var db = t.NewDb();
        var account = await db.Accounts.SingleAsync(a => a.EmailNormalized == email, Ct);
        Assert.Equal(Guid.Parse(jwt.Subject), account.Id);
        Assert.Equal(AccountTypes.Staff, account.Type);
        Assert.Equal(AccountStatuses.Active, account.Status);
        Assert.Equal(StaffRoles.Warehouse, account.Role);
        Assert.Equal("New Staff", account.Name);
        Assert.Equal(email, account.EmailDisplay);
        Assert.NotNull(account.PasswordHash);
        Assert.Equal(secret, t.Services.GetRequiredKeyedService<SecretBox>("totp").Open(account.TotpSecretEnc!));
        Assert.Equal(t.Clock.GetUtcNow(), (await db.StaffInvitations.SingleAsync(Ct)).AcceptedAt!.Value, TimeSpan.FromMilliseconds(1));

        // The enrolment code is spent; the next step's code signs in through the normal routes.
        var signIn = await t.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email, password = Seed.Password });
        var challenge = (await signIn.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("mfaChallengeId").GetString();
        var mfa = await t.SendAsync(HttpMethod.Post, "/api/auth/staff/mfa", new { mfaChallengeId = challenge, code = CodeAt(t, secret, 1) });
        Assert.Equal(HttpStatusCode.OK, mfa.StatusCode);
    }

    [Fact]
    public async Task Expired_used_or_unknown_invitation_is_INVITATION_INVALID()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var secret = RandomNumberGenerator.GetBytes(20);
        var (_, expired) = await InvitedAsync(t);
        var (_, used) = await InvitedAsync(t);

        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(t, used, ToBase32(secret), CodeAt(t, secret))).StatusCode);
        await (await AcceptAsync(t, used, ToBase32(secret), CodeAt(t, secret, 1))).ExpectErrorAsync(400, "INVITATION_INVALID");
        await (await AcceptAsync(t, Opaque.NewToken(), ToBase32(secret), CodeAt(t, secret))).ExpectErrorAsync(400, "INVITATION_INVALID");
        t.Clock.Advance(TimeSpan.FromHours(72) + TimeSpan.FromSeconds(1));
        await (await AcceptAsync(t, expired, ToBase32(secret), CodeAt(t, secret))).ExpectErrorAsync(400, "INVITATION_INVALID");
    }

    [Fact]
    public async Task Wrong_first_totp_keeps_the_invitation()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (email, token) = await InvitedAsync(t);
        var secret = RandomNumberGenerator.GetBytes(20);

        await (await AcceptAsync(t, token, ToBase32(secret), CodeAt(t, secret, 10))).ExpectErrorAsync(401, "MFA_INVALID");
        await using (var db = t.NewDb())
        {
            Assert.Null((await db.StaffInvitations.SingleAsync(Ct)).AcceptedAt);
            Assert.False(await db.Accounts.AnyAsync(a => a.EmailNormalized == email, Ct));
        }

        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(t, token, ToBase32(secret), CodeAt(t, secret))).StatusCode);
    }

    [Fact]
    public async Task Short_or_malformed_totp_secret_is_VALIDATION_FAILED()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (_, token) = await InvitedAsync(t);
        var short16 = RandomNumberGenerator.GetBytes(16);

        var tooShort = await (await AcceptAsync(t, token, ToBase32(short16), CodeAt(t, short16))).ExpectErrorAsync(400, "VALIDATION_FAILED");
        var malformed = await (await AcceptAsync(t, token, "!!!", "123456")).ExpectErrorAsync(400, "VALIDATION_FAILED");

        Assert.Equal("totp_secret", tooShort.GetProperty("metadata").GetProperty("field").GetString());
        Assert.Equal("totp_secret", malformed.GetProperty("metadata").GetProperty("field").GetString());
        await using var db = t.NewDb();
        Assert.Null((await db.StaffInvitations.SingleAsync(Ct)).AcceptedAt);
    }

    [Fact]
    public async Task Email_taken_before_accept_is_EMAIL_TAKEN()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (email, token) = await InvitedAsync(t, email: "late@bloom.example");
        await Seed.CustomerAsync(t, email);
        var secret = RandomNumberGenerator.GetBytes(20);

        await (await AcceptAsync(t, token, ToBase32(secret), CodeAt(t, secret))).ExpectErrorAsync(409, "EMAIL_TAKEN");

        await using var db = t.NewDb();
        Assert.Null((await db.StaffInvitations.SingleAsync(Ct)).AcceptedAt);
        Assert.Equal(AccountTypes.Customer, (await db.Accounts.SingleAsync(a => a.EmailNormalized == email, Ct)).Type);
    }

    [Fact]
    public async Task ListStaff_lists_staff_only()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (noMfa, _) = await Seed.StaffAsync(t, StaffRoles.Support, withTotp: false);
        var customer = await Seed.CustomerAsync(t);

        var response = await t.SendAsync(HttpMethod.Get, "/api/admin/staff/members", caller: AdminCaller(t, admin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var staff = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("staff").EnumerateArray().ToList();
        Assert.Equal(2, staff.Count);
        Assert.DoesNotContain(staff, s => s.GetProperty("accountId").GetString() == customer.Id.ToString());
        var a = staff.Single(s => s.GetProperty("accountId").GetString() == admin.Id.ToString());
        Assert.Equal(admin.EmailNormalized, a.GetProperty("email").GetString());
        Assert.Equal("Test Staff", a.GetProperty("name").GetString());
        Assert.Equal(StaffRoles.Admin, a.GetProperty("role").GetString());
        Assert.Equal("active", a.GetProperty("status").GetString());
        Assert.True(a.GetProperty("mfaEnrolled").GetBoolean());
        Assert.Equal(admin.CreatedAt, a.GetProperty("createdAt").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        Assert.False(a.TryGetProperty("lastSignInAt", out _));
        var b = staff.Single(s => s.GetProperty("accountId").GetString() == noMfa.Id.ToString());
        Assert.False(b.GetProperty("mfaEnrolled").GetBoolean());

        await (await t.SendAsync(HttpMethod.Get, "/api/admin/staff/members", caller: TestCaller.Staff(noMfa.Id, StaffRoles.Support, t.Clock.GetUtcNow())))
            .ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }
}
