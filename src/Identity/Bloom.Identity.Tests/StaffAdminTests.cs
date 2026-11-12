using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Bloom.Identity.Tests;

public class StaffAdminTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Redis => containers.Redis.GetDatabase();

    private static Task<HttpResponseMessage> ActAsync(TestIdentity t, Account admin, string id, string action, object? body = null) =>
        t.SendAsync(HttpMethod.Post, $"/api/admin/staff/members/{id}/{action}", body ?? new { }, TestCaller.Staff(admin.Id, StaffRoles.Admin, t.Clock.GetUtcNow()));

    private static Task<HttpResponseMessage> ChangeRoleAsync(TestIdentity t, Account admin, string id, string role) =>
        ActAsync(t, admin, id, "role", new { role });

    private static string InviteToken(Envelope envelope) => envelope.GetPayload<StaffInvitedV1>().InviteUrl.Split("?token=")[1];

    private static (string Before, string After) Details(AuditEntry entry)
    {
        using var details = JsonDocument.Parse(entry.Details);
        return (details.RootElement.GetProperty("before").GetString()!, details.RootElement.GetProperty("after").GetString()!);
    }

    [Fact]
    public async Task ID_T15_deactivating_the_last_admin_is_LAST_ADMIN()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (only, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        await Seed.SessionAsync(t, only);
        var version = (await t.AccountAsync(only.Id)).Version;

        await (await ActAsync(t, only, only.Id.ToString(), "deactivate")).ExpectErrorAsync(400, "LAST_ADMIN");
        await (await ChangeRoleAsync(t, only, only.Id.ToString(), StaffRoles.Support)).ExpectErrorAsync(400, "LAST_ADMIN");

        await using (var db = t.NewDb())
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == only.Id, Ct);
            Assert.Equal(AccountStatuses.Active, account.Status);
            Assert.Equal(StaffRoles.Admin, account.Role);
            Assert.Equal(version, account.Version);
            Assert.Empty(await db.AuditEntries.ToListAsync(Ct));
            Assert.All(await db.RefreshTokens.ToListAsync(Ct), r => Assert.Equal(RefreshStatuses.Active, r.Status));
        }
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        Assert.Contains(t.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("LAST_ADMIN"));

        // With a second admin the same change goes through.
        var (second, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await ChangeRoleAsync(t, second, only.Id.ToString(), StaffRoles.Support)).StatusCode);
        Assert.Equal(StaffRoles.Support, (await t.AccountAsync(only.Id)).Role);
    }

    [Fact]
    public async Task Two_admins_deactivating_each_other_leaves_one()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (a, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (b, _) = await Seed.StaffAsync(t, StaffRoles.Admin);

        var responses = await Task.WhenAll(ActAsync(t, a, b.Id.ToString(), "deactivate"), ActAsync(t, b, a.Id.ToString(), "deactivate"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK).ExpectErrorAsync(400, "LAST_ADMIN");
        await using var db = t.NewDb();
        Assert.Equal(1, await db.Accounts.CountAsync(x => x.Role == StaffRoles.Admin && x.Status == AccountStatuses.Active, Ct));
        Assert.Single(await db.AuditEntries.ToListAsync(Ct));
    }

    [Fact]
    public async Task Deactivate_takes_effect_at_once()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Warehouse);
        var session = await Seed.SessionAsync(t, staff);
        await Seed.SessionAsync(t, staff);
        var version = (await t.AccountAsync(staff.Id)).Version;

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(t, admin, staff.Id.ToString(), "deactivate")).StatusCode);

        await using (var db = t.NewDb())
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == staff.Id, Ct);
            Assert.Equal(AccountStatuses.Deactivated, account.Status);
            Assert.Equal(t.Clock.GetUtcNow(), account.TokensValidAfter!.Value, TimeSpan.FromMilliseconds(1));
            Assert.Equal(version + 1, account.Version);
            Assert.All(await db.RefreshTokens.Where(r => r.AccountId == staff.Id).ToListAsync(Ct), r => Assert.Equal(RefreshStatuses.Revoked, r.Status));
            var audit = await db.AuditEntries.SingleAsync(Ct);
            Assert.Equal(admin.Id, audit.ActorId);
            Assert.Equal(StaffRoles.Admin, audit.ActorRole);
            Assert.Equal("identity", audit.Service);
            Assert.Equal("identity.staff-deactivated", audit.Action);
            Assert.Equal("staff_account", audit.TargetType);
            Assert.Equal(staff.Id.ToString(), audit.TargetId);
            Assert.Equal(("active", "deactivated"), Details(audit));
        }
        var revoked = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked)).GetPayload<TokensRevokedV1>();
        Assert.Equal(staff.Id, revoked.AccountId);
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "staff_deactivated")));
        Assert.Equal(1, metrics.Sum("audit_entries_total", ("service", "identity")));

        await (await t.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email = staff.EmailNormalized, password = Seed.Password }))
            .ExpectErrorAsync(401, "INVALID_CREDENTIALS");
        await (await t.SendAsync(HttpMethod.Post, "/auth/refresh", cookie: $"bloom_rt={session.RefreshToken}")).ExpectErrorAsync(401, "REFRESH_INVALID");
    }

    [Fact]
    public async Task Repeat_deactivate_and_reactivate_are_no_ops()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Support);

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(t, admin, staff.Id.ToString(), "reactivate")).StatusCode); // already active
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(t, admin, staff.Id.ToString(), "deactivate")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(t, admin, staff.Id.ToString(), "deactivate")).StatusCode);

        await using var db = t.NewDb();
        Assert.Equal("identity.staff-deactivated", (await db.AuditEntries.SingleAsync(Ct)).Action);
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        Assert.Equal(2, (await db.Accounts.SingleAsync(a => a.Id == staff.Id, Ct)).Version);
    }

    [Fact]
    public async Task Reactivate_restores_sign_in()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Support, status: AccountStatuses.Deactivated);

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(t, admin, staff.Id.ToString(), "reactivate")).StatusCode);

        Assert.Equal(AccountStatuses.Active, (await t.AccountAsync(staff.Id)).Status);
        await using var db = t.NewDb();
        var audit = await db.AuditEntries.SingleAsync(Ct);
        Assert.Equal("identity.staff-reactivated", audit.Action);
        Assert.Equal(staff.Id.ToString(), audit.TargetId);
        Assert.Equal(("deactivated", "active"), Details(audit));
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        var signIn = await t.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email = staff.EmailNormalized, password = Seed.Password });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.True((await signIn.Content.ReadFromJsonAsync<JsonElement>(Ct)).TryGetProperty("mfaChallengeId", out _));
    }

    [Fact]
    public async Task Change_role_revokes_and_audits()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Support);
        await Seed.SessionAsync(t, staff);

        Assert.Equal(HttpStatusCode.OK, (await ChangeRoleAsync(t, admin, staff.Id.ToString(), StaffRoles.Support)).StatusCode); // same role: no-op
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        Assert.Equal(HttpStatusCode.OK, (await ChangeRoleAsync(t, admin, staff.Id.ToString(), StaffRoles.CatalogManager)).StatusCode);

        Assert.Equal(StaffRoles.CatalogManager, (await t.AccountAsync(staff.Id)).Role);
        await using var db = t.NewDb();
        Assert.All(await db.RefreshTokens.Where(r => r.AccountId == staff.Id).ToListAsync(Ct), r => Assert.Equal(RefreshStatuses.Revoked, r.Status));
        var audit = await db.AuditEntries.SingleAsync(Ct);
        Assert.Equal("identity.staff-role-changed", audit.Action);
        Assert.Equal(staff.Id.ToString(), audit.TargetId);
        Assert.Equal((StaffRoles.Support, StaffRoles.CatalogManager), Details(audit));
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "staff_role_changed")));
    }

    [Fact]
    public async Task Staff_actions_need_a_staff_target()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Support);
        var customer = await Seed.CustomerAsync(t);

        foreach (var id in new[] { customer.Id.ToString(), Guid.NewGuid().ToString(), "not-a-uuid" })
        {
            await (await ChangeRoleAsync(t, admin, id, StaffRoles.Support)).ExpectErrorAsync(404, "NOT_FOUND");
            foreach (var action in new[] { "deactivate", "reactivate", "reset-mfa" })
                await (await ActAsync(t, admin, id, action)).ExpectErrorAsync(404, "NOT_FOUND");
        }
        var bad = await (await ChangeRoleAsync(t, admin, staff.Id.ToString(), "owner")).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("role", bad.GetProperty("metadata").GetProperty("field").GetString());
        await (await t.SendAsync(HttpMethod.Post, $"/api/admin/staff/members/{staff.Id}/deactivate", new { },
            TestCaller.Staff(staff.Id, StaffRoles.Support, t.Clock.GetUtcNow()))).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");

        await using var db = t.NewDb();
        Assert.Empty(await db.AuditEntries.ToListAsync(Ct));
        Assert.Equal(AccountStatuses.Active, (await db.Accounts.SingleAsync(a => a.Id == customer.Id, Ct)).Status);
    }

    [Fact]
    public async Task Reset_mfa_sends_reenrol_link()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Warehouse);
        await Seed.SessionAsync(t, staff);

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(t, admin, staff.Id.ToString(), "reset-mfa")).StatusCode);

        await using var db = t.NewDb();
        Assert.Null((await db.Accounts.SingleAsync(a => a.Id == staff.Id, Ct)).TotpSecretEnc);
        Assert.All(await db.RefreshTokens.Where(r => r.AccountId == staff.Id).ToListAsync(Ct), r => Assert.Equal(RefreshStatuses.Revoked, r.Status));
        var invitation = await db.StaffInvitations.SingleAsync(Ct);
        Assert.Equal(staff.EmailNormalized, invitation.EmailNormalized);
        Assert.Equal(StaffRoles.Warehouse, invitation.Role);
        Assert.Equal(admin.Id, invitation.InvitedBy);
        var envelope = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.StaffInvited));
        var message = envelope.GetPayload<StaffInvitedV1>();
        Assert.Equal(staff.EmailNormalized, message.Email);
        Assert.Equal(StaffRoles.Warehouse, message.Role);
        Assert.Equal(Opaque.Sha256(InviteToken(envelope)), invitation.TokenHash);
        var audit = await db.AuditEntries.SingleAsync(Ct);
        Assert.Equal("identity.staff-mfa-reset", audit.Action);
        Assert.Equal(staff.Id.ToString(), audit.TargetId);
        Assert.Equal("{}", audit.Details);
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Message.Contains(InviteToken(envelope)));

        await (await t.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email = staff.EmailNormalized, password = Seed.Password }))
            .ExpectErrorAsync(401, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Reenrol_needs_current_password_and_new_totp()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Warehouse);
        await ActAsync(t, admin, staff.Id.ToString(), "reset-mfa");
        var token = InviteToken((await Outbox.EnvelopesAsync(t, IdentityMessages.StaffInvited)).Single());
        var (text, secret) = TestTotp.NewSecret();
        Task<HttpResponseMessage> Accept(string currentPassword, string code, string? totpText = null) =>
            t.SendAsync(HttpMethod.Post, "/api/auth/staff/invitations/accept",
                new { inviteToken = token, currentPassword, totpSecret = totpText ?? text, totpCode = code });

        await (await Accept("Wrong-password-99", TestTotp.CodeAt(t, secret))).ExpectErrorAsync(401, "INVALID_CREDENTIALS");
        Assert.Equal(1, (long)await Redis.StringGetAsync($"signin:fail:{staff.Id}"));
        await (await Accept(Seed.Password, TestTotp.CodeAt(t, secret, 10))).ExpectErrorAsync(401, "MFA_INVALID");
        var (weakText, weak) = TestTotp.NewSecret(26);
        var field = await (await Accept(Seed.Password, TestTotp.CodeAt(t, weak), weakText)).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("totp_secret", field.GetProperty("metadata").GetProperty("field").GetString());
        await using (var db = t.NewDb())
        {
            Assert.Null((await db.StaffInvitations.SingleAsync(Ct)).AcceptedAt);
            Assert.Null((await db.Accounts.SingleAsync(a => a.Id == staff.Id, Ct)).TotpSecretEnc);
        }

        var response = await Accept(Seed.Password, TestTotp.CodeAt(t, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(response.RefreshCookie()));
        Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()));
        Assert.False(await Redis.KeyExistsAsync($"signin:fail:{staff.Id}")); // the right password clears the counter
        await using (var db = t.NewDb())
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == staff.Id, Ct);
            Assert.Equal(secret, t.Services.GetRequiredKeyedService<SecretBox>("totp").Open(account.TotpSecretEnc!));
            Assert.Equal("Test Staff", account.Name);
            Assert.Equal(StaffRoles.Warehouse, account.Role);
            Assert.Equal(staff.PasswordHash, account.PasswordHash);
            Assert.Equal(t.Clock.GetUtcNow(), (await db.StaffInvitations.SingleAsync(Ct)).AcceptedAt!.Value, TimeSpan.FromMilliseconds(1));
        }
        await (await Accept(Seed.Password, TestTotp.CodeAt(t, secret, 1))).ExpectErrorAsync(400, "INVITATION_INVALID");
    }

    [Fact]
    public async Task Leftover_reenrol_link_of_an_enrolled_staff_account_is_INVITATION_INVALID() // R43
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, current) = await Seed.StaffAsync(t, StaffRoles.Warehouse);
        await ActAsync(t, admin, staff.Id.ToString(), "reset-mfa");
        var token = InviteToken((await Outbox.EnvelopesAsync(t, IdentityMessages.StaffInvited)).Single());
        await using (var db = t.NewDb()) // enrolled again some other way: the first link is left over
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == staff.Id, Ct);
            account.TotpSecretEnc = t.Services.GetRequiredKeyedService<SecretBox>("totp").Seal(current);
            await db.SaveChangesAsync(Ct);
        }
        var sealedSecret = (await t.AccountAsync(staff.Id)).TotpSecretEnc;
        var (text, secret) = TestTotp.NewSecret();

        foreach (var password in new[] { Seed.Password, "Wrong-password-99" })
            await (await t.SendAsync(HttpMethod.Post, "/api/auth/staff/invitations/accept",
                new { inviteToken = token, currentPassword = password, totpSecret = text, totpCode = TestTotp.CodeAt(t, secret) }))
                .ExpectErrorAsync(400, "INVITATION_INVALID");

        Assert.Equal(sealedSecret, (await t.AccountAsync(staff.Id)).TotpSecretEnc);
        Assert.False(await Redis.KeyExistsAsync($"signin:fail:{staff.Id}"));
        await using var check = t.NewDb();
        Assert.Null((await check.StaffInvitations.SingleAsync(Ct)).AcceptedAt);
    }

    [Fact]
    public async Task Reenrol_needs_an_active_account()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Warehouse);
        await ActAsync(t, admin, staff.Id.ToString(), "reset-mfa");
        var token = InviteToken((await Outbox.EnvelopesAsync(t, IdentityMessages.StaffInvited)).Single());
        await ActAsync(t, admin, staff.Id.ToString(), "deactivate");
        var (text, secret) = TestTotp.NewSecret();

        await (await t.SendAsync(HttpMethod.Post, "/api/auth/staff/invitations/accept",
            new { inviteToken = token, currentPassword = Seed.Password, totpSecret = text, totpCode = TestTotp.CodeAt(t, secret) }))
            .ExpectErrorAsync(401, "INVALID_CREDENTIALS");

        await using var db = t.NewDb();
        Assert.Null((await db.StaffInvitations.SingleAsync(Ct)).AcceptedAt);
        Assert.Null((await db.Accounts.SingleAsync(a => a.Id == staff.Id, Ct)).TotpSecretEnc);
    }

    [Fact]
    public async Task Revocation_triggers_are_counted()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Support);

        await ChangeRoleAsync(t, admin, staff.Id.ToString(), StaffRoles.Warehouse);
        await ActAsync(t, admin, staff.Id.ToString(), "reset-mfa");
        await ActAsync(t, admin, staff.Id.ToString(), "deactivate");

        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "staff_role_changed")));
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "staff_mfa_reset")));
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "staff_deactivated")));
    }
}
