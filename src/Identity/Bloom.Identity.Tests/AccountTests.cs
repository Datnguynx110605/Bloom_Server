using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Bloom.Identity.Tests;

public class AccountTests(Containers containers)
{
    private const string NewPassword = "Another-horse-77";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Redis => containers.Redis.GetDatabase();

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private static TestCaller Stale(TestIdentity t, Account a) => TestCaller.Customer(a.Id, t.Clock.GetUtcNow().AddMinutes(-10));

    private static Task<HttpResponseMessage> ChangePasswordAsync(TestIdentity t, TestCaller caller, string current, string next) =>
        t.SendAsync(HttpMethod.Post, "/api/me/password", new { currentPassword = current, newPassword = next }, caller);

    private static Task<HttpResponseMessage> ChangeEmailAsync(TestIdentity t, TestCaller caller, string newEmail) =>
        t.SendAsync(HttpMethod.Post, "/api/me/email", new { newEmail }, caller);

    private static Task<HttpResponseMessage> ConfirmEmailAsync(TestIdentity t, TestCaller caller, string code) =>
        t.SendAsync(HttpMethod.Post, "/api/me/email/confirm", new { code }, caller);

    private static Task<HttpResponseMessage> RequestResetAsync(TestIdentity t, string email) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/password-reset/request", new { email });

    private static Task<HttpResponseMessage> ResetAsync(TestIdentity t, string email, string code, string newPassword) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/password-reset/confirm", new { email, code, newPassword });

    private static Task<HttpResponseMessage> SignInAsync(TestIdentity t, string email, string password) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email, password });

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(Ct);

    [Fact]
    public async Task GetMe_returns_profile()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var customer = await Seed.CustomerAsync(t, email: "Ann@Example.COM");
        var googleOnly = await Seed.CustomerAsync(t, password: null, googleSub: "sub-1");
        var (staff, _) = await Seed.StaffAsync(t, StaffRoles.Admin);

        var me = await (await t.SendAsync(HttpMethod.Get, "/api/me/profile", caller: TestCaller.Customer(t, customer))).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(customer.Id.ToString("D"), me.GetProperty("accountId").GetString());
        Assert.Equal("customer", me.GetProperty("type").GetString());
        Assert.Equal("Ann@Example.COM", me.GetProperty("email").GetString());
        Assert.Equal("Test Customer", me.GetProperty("name").GetString());
        Assert.True(me.GetProperty("hasPassword").GetBoolean());
        Assert.False(me.TryGetProperty("googleLinked", out var linked) && linked.GetBoolean());
        Assert.False(me.TryGetProperty("role", out _));

        var google = await (await t.SendAsync(HttpMethod.Get, "/api/me/profile", caller: TestCaller.Customer(t, googleOnly))).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(google.GetProperty("googleLinked").GetBoolean());
        Assert.False(google.TryGetProperty("hasPassword", out var has) && has.GetBoolean());

        var admin = await (await t.SendAsync(HttpMethod.Get, "/api/me/profile", caller: TestCaller.Staff(staff.Id, "admin", t.Clock.GetUtcNow())))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("staff", admin.GetProperty("type").GetString());
        Assert.Equal("admin", admin.GetProperty("role").GetString());
    }

    [Fact]
    public async Task ID_T12_change_password_with_10_minute_old_auth_is_REAUTH_REQUIRED()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);

        await (await ChangePasswordAsync(t, Stale(t, account), Seed.Password, NewPassword)).ExpectErrorAsync(400, "REAUTH_REQUIRED");

        Assert.Equal(account.PasswordHash, (await t.AccountAsync(account.Id)).PasswordHash);
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
    }

    [Fact]
    public async Task Change_password_revokes_all_sessions()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        await Seed.SessionAsync(t, account);
        await Seed.SessionAsync(t, account);

        var before = await t.DbNowAsync();
        var response = await ChangePasswordAsync(t, TestCaller.Customer(t, account), Seed.Password, NewPassword);
        var after = await t.DbNowAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await using (var db = t.NewDb())
        {
            var tokens = await db.RefreshTokens.Where(x => x.AccountId == account.Id).ToListAsync(Ct);
            Assert.Equal(2, tokens.Count);
            Assert.All(tokens, x => Assert.Equal(RefreshStatuses.Revoked, x.Status));
        }
        var stored = await t.AccountAsync(account.Id);
        Assert.InRange(stored.TokensValidAfter!.Value, before, after);
        var revoked = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked)).GetPayload<TokensRevokedV1>();
        Assert.Equal(account.Id, revoked.AccountId);
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "password_change")));

        await (await SignInAsync(t, account.EmailNormalized, Seed.Password)).ExpectErrorAsync(401, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(t, account.EmailNormalized, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Change_password_wrong_current_counts_toward_lockout()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);

        await (await ChangePasswordAsync(t, TestCaller.Customer(t, account), "Wrong-horse-4242", NewPassword)).ExpectErrorAsync(401, "INVALID_CREDENTIALS");
        Assert.Equal(1, (long)await Redis.StringGetAsync($"signin:fail:{account.Id}"));

        // The new password is checked first: a weak one never reaches the current-password check or the counter.
        await (await ChangePasswordAsync(t, TestCaller.Customer(t, account), "Wrong-horse-4242", "short")).ExpectErrorAsync(400, "PASSWORD_WEAK");
        Assert.Equal(1, (long)await Redis.StringGetAsync($"signin:fail:{account.Id}"));
        Assert.Equal(account.PasswordHash, (await t.AccountAsync(account.Id)).PasswordHash);
    }

    [Fact]
    public async Task Change_email_flow()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        var next = NewEmail();

        var before = await t.DbNowAsync();
        var request = await ChangeEmailAsync(t, TestCaller.Customer(t, account), $" {next.ToUpperInvariant()} ");
        var after = await t.DbNowAsync();
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        var sent = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode)).GetPayload<VerificationCodeV1>();
        Assert.Equal(next, sent.Email);
        Assert.Equal("change_email", sent.Purpose);
        Assert.InRange(sent.ExpiresAt, before.AddMinutes(15), after.AddMinutes(15));

        var code = await Outbox.LatestCodeAsync(t, next, "change_email");
        Assert.Equal(HttpStatusCode.OK, (await ConfirmEmailAsync(t, TestCaller.Customer(t, account), code)).StatusCode);

        var stored = await t.AccountAsync(account.Id);
        Assert.Equal(next, stored.EmailNormalized);
        Assert.Equal(next, stored.EmailDisplay);
        var changed = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.ContactChanged)).GetPayload<ContactChangedV1>();
        Assert.Equal(new ContactChangedV1(account.Id, next, "Test Customer"), changed);

        // The old address is free again, and the code can't be used twice.
        Assert.Equal(HttpStatusCode.OK, (await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/start", new { email = account.EmailNormalized })).StatusCode);
        await (await ConfirmEmailAsync(t, TestCaller.Customer(t, account), code)).ExpectErrorAsync(400, "CODE_INVALID");
    }

    [Fact]
    public async Task Change_email_to_taken_email_is_EMAIL_TAKEN()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        var other = await Seed.CustomerAsync(t);

        foreach (var typed in new[] { other.EmailNormalized, other.EmailNormalized.ToUpperInvariant(), $" {other.EmailNormalized.ToUpperInvariant()} " })
            await (await ChangeEmailAsync(t, TestCaller.Customer(t, account), typed)).ExpectErrorAsync(409, "EMAIL_TAKEN");
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode));

        // Taken in between request and confirm.
        var next = NewEmail();
        Assert.Equal(HttpStatusCode.OK, (await ChangeEmailAsync(t, TestCaller.Customer(t, account), next)).StatusCode);
        await Seed.CustomerAsync(t, email: next);
        var code = await Outbox.LatestCodeAsync(t, next, "change_email");

        await (await ConfirmEmailAsync(t, TestCaller.Customer(t, account), code)).ExpectErrorAsync(409, "EMAIL_TAKEN");

        Assert.Equal(account.EmailNormalized, (await t.AccountAsync(account.Id)).EmailNormalized);
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.ContactChanged));
    }

    [Fact]
    public async Task Email_change_is_customer_only_and_needs_reauth()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (staff, _) = await Seed.StaffAsync(t);
        var staffCaller = TestCaller.Staff(staff.Id, staff.Role!, t.Clock.GetUtcNow());

        await (await ChangeEmailAsync(t, staffCaller, NewEmail())).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
        var invalid = await (await ChangeEmailAsync(t, TestCaller.Customer(Guid.NewGuid(), t.Clock.GetUtcNow()), "not-an-email")).ExpectErrorAsync(400, "VALIDATION_FAILED");
        Assert.Equal("new_email", invalid.GetProperty("metadata").GetProperty("field").GetString());
        await (await ConfirmEmailAsync(t, staffCaller, "123456")).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");

        var account = await Seed.CustomerAsync(t);
        var next = NewEmail();
        await (await ChangeEmailAsync(t, Stale(t, account), next)).ExpectErrorAsync(400, "REAUTH_REQUIRED");
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode));

        Assert.Equal(HttpStatusCode.OK, (await ChangeEmailAsync(t, TestCaller.Customer(t, account), next)).StatusCode);
        var code = await Outbox.LatestCodeAsync(t, next, "change_email");
        await (await ConfirmEmailAsync(t, Stale(t, account), code)).ExpectErrorAsync(400, "REAUTH_REQUIRED");

        // The refused confirm spent nothing: the same code works with a fresh sign-in.
        Assert.Equal(HttpStatusCode.OK, (await ConfirmEmailAsync(t, TestCaller.Customer(t, account), code)).StatusCode);
    }

    [Fact]
    public async Task ID_T11_reset_for_unknown_email_looks_the_same_and_sends_nothing()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var known = await Seed.CustomerAsync(t);

        var knownResponse = await RequestResetAsync(t, known.EmailNormalized);
        Assert.Equal(HttpStatusCode.OK, knownResponse.StatusCode);
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode));
        Assert.Equal(1, metrics.Sum("codes_sent_total", ("purpose", "reset_password")));

        var unknownResponse = await RequestResetAsync(t, NewEmail());

        Assert.Equal(knownResponse.StatusCode, unknownResponse.StatusCode);
        Assert.Equal(await BodyAsync(knownResponse), await BodyAsync(unknownResponse));
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode));
        Assert.Equal(1, metrics.Sum("codes_sent_total", ("purpose", "reset_password")));

        // A deactivated account is treated like an unknown one.
        var deactivated = await Seed.CustomerAsync(t);
        await using (var db = t.NewDb())
            await db.Accounts.Where(a => a.Id == deactivated.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AccountStatuses.Deactivated), Ct);
        Assert.Equal(HttpStatusCode.OK, (await RequestResetAsync(t, deactivated.EmailNormalized)).StatusCode);
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode));
    }

    [Fact]
    public async Task Password_reset_flow()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        await Seed.SessionAsync(t, account);

        var before = await t.DbNowAsync();
        var requested = await RequestResetAsync(t, $" {account.EmailNormalized.ToUpperInvariant()} ");
        var after = await t.DbNowAsync();
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        var sent = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode)).GetPayload<VerificationCodeV1>();
        Assert.Equal(account.EmailNormalized, sent.Email);
        Assert.Equal("reset_password", sent.Purpose);
        Assert.InRange(sent.ExpiresAt, before.AddMinutes(10), after.AddMinutes(10));

        var code = await Outbox.LatestCodeAsync(t, account.EmailNormalized, "reset_password");
        await using (var db = t.NewDb()) // the HMAC subject is the account id (spec §7, R45)
            Assert.Equal(account.Id, (await db.VerificationCodes.SingleAsync(Ct)).AccountId);
        var reset = await ResetAsync(t, account.EmailNormalized, code, NewPassword);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(reset.Headers.Contains("Set-Cookie"));
        await using (var db = t.NewDb())
            Assert.All(await db.RefreshTokens.Where(x => x.AccountId == account.Id).ToListAsync(Ct), x => Assert.Equal(RefreshStatuses.Revoked, x.Status));
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "password_reset")));
        await (await SignInAsync(t, account.EmailNormalized, Seed.Password)).ExpectErrorAsync(401, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(t, account.EmailNormalized, NewPassword)).StatusCode);

        await (await ResetAsync(t, account.EmailNormalized, code, NewPassword)).ExpectErrorAsync(400, "CODE_INVALID");
    }

    [Fact]
    public async Task Reset_for_unknown_email_and_wrong_code_are_CODE_INVALID()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await RequestResetAsync(t, account.EmailNormalized)).StatusCode);
        var code = await Outbox.LatestCodeAsync(t, account.EmailNormalized, "reset_password");
        var wrong = ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

        await (await ResetAsync(t, NewEmail(), "123456", NewPassword)).ExpectErrorAsync(400, "CODE_INVALID");
        await (await ResetAsync(t, account.EmailNormalized, wrong, NewPassword)).ExpectErrorAsync(400, "CODE_INVALID");
        Assert.Equal(account.PasswordHash, (await t.AccountAsync(account.Id)).PasswordHash);

        // Deactivated after the code went out: the code no longer works.
        await using (var db = t.NewDb())
            await db.Accounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AccountStatuses.Deactivated), Ct);
        await (await ResetAsync(t, account.EmailNormalized, code, NewPassword)).ExpectErrorAsync(400, "CODE_INVALID");
        Assert.Equal(account.PasswordHash, (await t.AccountAsync(account.Id)).PasswordHash);
    }

    [Fact]
    public async Task Reset_rate_limit_does_not_reveal_accounts()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var known = await Seed.CustomerAsync(t);

        async Task<List<int>> SixRequestsAsync(string email)
        {
            var statuses = new List<int>();
            for (var i = 0; i < 6; i++)
            {
                await Redis.KeyDeleteAsync($"code:cooldown:{email}:reset_password"); // Redis TTLs are real time (I3)
                var response = await RequestResetAsync(t, email);
                statuses.Add((int)response.StatusCode);
                if (i == 5) await response.ExpectErrorAsync(429, "CODE_RATE_LIMITED");
            }
            return statuses;
        }

        var unknownStatuses = await SixRequestsAsync(NewEmail());
        var knownStatuses = await SixRequestsAsync(known.EmailNormalized);

        Assert.Equal([200, 200, 200, 200, 200, 429], unknownStatuses);
        Assert.Equal(unknownStatuses, knownStatuses);

        // The cooldown answers the same for both as well.
        var cooldownEmail = NewEmail();
        Assert.Equal(HttpStatusCode.OK, (await RequestResetAsync(t, cooldownEmail)).StatusCode);
        await (await RequestResetAsync(t, cooldownEmail)).ExpectErrorAsync(429, "CODE_RATE_LIMITED");
    }

    [Fact]
    public async Task Weak_reset_password_spends_no_attempt()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await RequestResetAsync(t, account.EmailNormalized)).StatusCode);
        var code = await Outbox.LatestCodeAsync(t, account.EmailNormalized, "reset_password");

        await (await ResetAsync(t, account.EmailNormalized, code, "short")).ExpectErrorAsync(400, "PASSWORD_WEAK");

        await using (var db = t.NewDb())
            Assert.Equal(0, (await db.VerificationCodes.SingleAsync(c => c.TargetEmail == account.EmailNormalized, Ct)).Attempts);
        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(t, account.EmailNormalized, code, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Two_parallel_resets_with_one_code_succeed_once()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await RequestResetAsync(t, account.EmailNormalized)).StatusCode);
        var code = await Outbox.LatestCodeAsync(t, account.EmailNormalized, "reset_password");

        var responses = await Task.WhenAll(
            ResetAsync(t, account.EmailNormalized, code, NewPassword),
            ResetAsync(t, account.EmailNormalized, code, "Third-horse-9999"));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var failed = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK);
        await failed.ExpectErrorAsync(400, "CODE_INVALID");
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
    }

    [Fact]
    public async Task Change_email_code_sent_is_counted()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);

        Assert.Equal(HttpStatusCode.OK, (await ChangeEmailAsync(t, TestCaller.Customer(t, account), NewEmail())).StatusCode);

        Assert.Equal(1, metrics.Sum("codes_sent_total", ("purpose", "change_email")));
    }

    [Fact]
    public async Task Wrong_code_is_counted_per_purpose()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        var caller = TestCaller.Customer(t, account);
        var (signUp, next) = (NewEmail(), NewEmail());
        static string Wrong(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

        await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/start", new { email = signUp });
        await (await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/verify", new { email = signUp, code = Wrong(await Outbox.LatestCodeAsync(t, signUp, "signup")) }))
            .ExpectErrorAsync(400, "CODE_INVALID");
        await RequestResetAsync(t, account.EmailNormalized);
        await (await ResetAsync(t, account.EmailNormalized, Wrong(await Outbox.LatestCodeAsync(t, account.EmailNormalized, "reset_password")), NewPassword))
            .ExpectErrorAsync(400, "CODE_INVALID");
        await ChangeEmailAsync(t, caller, next);
        await (await ConfirmEmailAsync(t, caller, Wrong(await Outbox.LatestCodeAsync(t, next, "change_email")))).ExpectErrorAsync(400, "CODE_INVALID");

        foreach (var purpose in new[] { "signup", "reset_password", "change_email" })
            Assert.Equal(1, metrics.Sum("code_failures_total", ("purpose", purpose)));
    }
}
