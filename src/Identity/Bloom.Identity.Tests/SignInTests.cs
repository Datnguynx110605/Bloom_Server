using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using StackExchange.Redis;

namespace Bloom.Identity.Tests;

public class SignInTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Redis => containers.Redis.GetDatabase();

    private static Task<HttpResponseMessage> SignInAsync(TestIdentity t, string email, string password) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email, password });

    private static Task<HttpResponseMessage> MfaAsync(TestIdentity t, string challengeId, string code) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/staff/mfa", new { mfaChallengeId = challengeId, code });

    /// <summary>The whole error body, so two answers can be compared byte for byte.</summary>
    private static async Task<string> InvalidBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        await response.ExpectErrorAsync(401, "INVALID_CREDENTIALS");
        return body;
    }

    private static string WrongPassword => "Wrong-horse-4242";

    private static async Task<string> ChallengeAsync(TestIdentity t, Account staff)
    {
        var response = await SignInAsync(t, staff.EmailNormalized, Seed.Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.OkJsonAsync()).GetProperty("mfaChallengeId").GetString()!;
    }

    private static long Verifications(TestIdentity t) => t.Services.GetRequiredService<PasswordHasher>().Verifications;

    [Fact]
    public async Task Customer_sign_in_issues_tokens()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);

        var response = await SignInAsync(t, account.EmailNormalized, Seed.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.OkJsonAsync()).GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(response.RefreshCookie()));
        await using var db = t.NewDb();
        var stored = await db.Accounts.SingleAsync(a => a.Id == account.Id, Ct);
        Assert.Equal(t.Clock.GetUtcNow(), stored.LastSignInAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, metrics.Sum("signins_total", ("type", "customer"), ("method", "password"), ("result", "success")));
        Assert.Equal(1, metrics.Count("signin_seconds"));
    }

    [Fact]
    public async Task ID_T02_five_wrong_passwords_block_for_15_minutes()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);

        string? wrongBody = null;
        for (var i = 0; i < 5; i++) wrongBody = await InvalidBodyAsync(await SignInAsync(t, account.EmailNormalized, WrongPassword));

        Assert.Equal(wrongBody, await InvalidBodyAsync(await SignInAsync(t, account.EmailNormalized, Seed.Password)));
        var ttl = (await Redis.KeyTimeToLiveAsync($"signin:block:{account.Id}"))!.Value.TotalSeconds;
        Assert.InRange(ttl, 880, 900);
        Assert.False(await Redis.KeyExistsAsync($"signin:fail:{account.Id}"));

        await Redis.KeyDeleteAsync($"signin:block:{account.Id}");
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(t, account.EmailNormalized, Seed.Password)).StatusCode);
    }

    [Fact]
    public async Task ID_T03_unknown_email_runs_one_verification()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        var wrongBody = await InvalidBodyAsync(await SignInAsync(t, account.EmailNormalized, WrongPassword));

        var before = Verifications(t);
        var unknownBody = await InvalidBodyAsync(await SignInAsync(t, "nobody@example.com", Seed.Password));
        Assert.Equal(before + 1, Verifications(t));
        Assert.Equal(wrongBody, unknownBody);

        // A malformed address is just another unknown one: sign-in never says "invalid email".
        Assert.Equal(wrongBody, await InvalidBodyAsync(await SignInAsync(t, "not an email", Seed.Password)));
        Assert.Equal(before + 2, Verifications(t));
    }

    [Fact]
    public async Task Blocked_deactivated_and_google_only_accounts_look_like_wrong_password()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var unknownBody = await InvalidBodyAsync(await SignInAsync(t, "nobody@example.com", Seed.Password));

        var blocked = await Seed.CustomerAsync(t);
        await Redis.StringSetAsync($"signin:block:{blocked.Id}", 1, TimeSpan.FromMinutes(15));
        var deactivated = await Seed.CustomerAsync(t);
        await using (var db = t.NewDb())
            await db.Accounts.Where(a => a.Id == deactivated.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AccountStatuses.Deactivated), Ct);
        var googleOnly = await Seed.CustomerAsync(t, password: null, googleSub: "google-sub-1");

        foreach (var account in new[] { blocked, deactivated, googleOnly })
        {
            var before = Verifications(t);
            var response = await SignInAsync(t, account.EmailNormalized, Seed.Password);
            Assert.Equal(unknownBody, await InvalidBodyAsync(response));
            Assert.Equal(before + 1, Verifications(t));
            Assert.Null(response.RefreshCookie());
        }
    }

    [Fact]
    public async Task ID_T04_staff_sign_in_without_totp_gets_no_tokens()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (staff, _) = await Seed.StaffAsync(t);

        var response = await SignInAsync(t, staff.EmailNormalized, Seed.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.OkJsonAsync();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("mfaChallengeId").GetString()));
        Assert.False(body.TryGetProperty("accessToken", out _));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await using var db = t.NewDb();
        Assert.False(await db.RefreshTokens.AnyAsync(r => r.AccountId == staff.Id, Ct));
        Assert.Equal(1, metrics.Sum("signins_total", ("type", "staff"), ("method", "password"), ("result", "mfa_required")));
    }

    [Fact]
    public async Task Staff_mfa_issues_staff_session()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (staff, secret) = await Seed.StaffAsync(t, StaffRoles.Warehouse);
        var challenge = await ChallengeAsync(t, staff);

        var response = await MfaAsync(t, challenge, TestTotp.CodeAt(t, secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(response.RefreshCookie()));
        var jwt = new JsonWebToken((await response.OkJsonAsync()).GetProperty("accessToken").GetString()!);
        Assert.Equal("staff", jwt.GetClaim("typ").Value);
        Assert.Equal(StaffRoles.Warehouse, jwt.GetClaim("role").Value);
        Assert.Equal(staff.Id.ToString(), jwt.Subject);
        await using var db = t.NewDb();
        var row = await db.RefreshTokens.SingleAsync(r => r.AccountId == staff.Id, Ct);
        Assert.Equal(t.Clock.GetUtcNow(), row.AuthTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal(row.AuthTime.AddHours(12), row.ExpiresAt);
        Assert.Equal(1, metrics.Sum("signins_total", ("type", "staff"), ("method", "mfa"), ("result", "success")));
        Assert.False(await Redis.KeyExistsAsync($"mfa:challenge:{challenge}")); // success deletes the challenge
    }

    [Fact]
    public async Task Totp_code_cannot_be_reused_in_its_window()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (staff, secret) = await Seed.StaffAsync(t);
        var code = TestTotp.CodeAt(t, secret);

        Assert.Equal(HttpStatusCode.OK, (await MfaAsync(t, await ChallengeAsync(t, staff), code)).StatusCode);

        await (await MfaAsync(t, await ChallengeAsync(t, staff), code)).ExpectErrorAsync(401, "MFA_INVALID");
    }

    [Fact]
    public async Task Five_wrong_totp_codes_block_mfa()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (staff, secret) = await Seed.StaffAsync(t);
        var challenge = await ChallengeAsync(t, staff);

        for (var i = 0; i < 5; i++) await (await MfaAsync(t, challenge, TestTotp.CodeAt(t, secret, 100))).ExpectErrorAsync(401, "MFA_INVALID");

        Assert.True(await Redis.KeyExistsAsync($"mfa:block:{staff.Id}"));
        Assert.False(await Redis.KeyExistsAsync($"mfa:challenge:{challenge}")); // exhausted
        await (await MfaAsync(t, challenge, TestTotp.CodeAt(t, secret))).ExpectErrorAsync(401, "MFA_INVALID");
        await (await MfaAsync(t, await ChallengeAsync(t, staff), TestTotp.CodeAt(t, secret))).ExpectErrorAsync(401, "MFA_INVALID"); // blocked account
        await (await MfaAsync(t, "no-such-challenge", TestTotp.CodeAt(t, secret))).ExpectErrorAsync(401, "MFA_INVALID");
    }

    [Fact]
    public async Task Staff_without_totp_secret_cannot_sign_in()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var (staff, _) = await Seed.StaffAsync(t, withTotp: false);

        var response = await SignInAsync(t, staff.EmailNormalized, Seed.Password);
        await InvalidBodyAsync(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(await Redis.KeyExistsAsync($"signin:fail:{staff.Id}"));

        await InvalidBodyAsync(await SignInAsync(t, staff.EmailNormalized, WrongPassword));
        Assert.Equal(1, (long)await Redis.StringGetAsync($"signin:fail:{staff.Id}"));
    }

    [Fact]
    public async Task Weaker_hash_is_upgraded_on_sign_in()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        var weaker = Sodium.PasswordHash.ArgonHashString(Seed.Password, 3, 33_554_432); // t = 3, m = 32 MiB (R6)
        await using (var db = t.NewDb())
            await db.Accounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, weaker), Ct);

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(t, account.EmailNormalized, Seed.Password)).StatusCode);

        await using var check = t.NewDb();
        var hash = (await check.Accounts.SingleAsync(a => a.Id == account.Id, Ct)).PasswordHash!;
        Assert.Contains("m=65536,t=3", hash);
        Assert.NotEqual(weaker, hash);
    }

    [Fact]
    public async Task Nfkc_equivalent_password_signs_in()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(t, account.EmailNormalized, "Ｃorrect-horse-42")).StatusCode);
    }

    [Fact]
    public async Task Email_case_does_not_matter_for_sign_in()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        await Seed.CustomerAsync(t, email: "Ann.Lee@Example.COM");

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(t, " ann.lee@EXAMPLE.com ", Seed.Password)).StatusCode);
    }
}
