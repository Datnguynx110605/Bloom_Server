using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Bloom.Identity.Tests;

public class SignUpTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private static Task<HttpResponseMessage> StartAsync(TestIdentity t, string email) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/start", new { email });

    private static Task<HttpResponseMessage> VerifyAsync(TestIdentity t, string email, string code) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/verify", new { email, code });

    private static Task<HttpResponseMessage> CompleteAsync(TestIdentity t, string token, string password = Seed.Password, string name = "Ann Lee", object? body = null) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/complete", body ?? new { registrationToken = token, password, name });

    /// <summary>Start and verify; <paramref name="email"/> is used as typed, the code is read for its normalized form.</summary>
    private static async Task<string> RegistrationTokenAsync(TestIdentity t, string email)
    {
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(t, email)).StatusCode);
        var code = await Outbox.LatestCodeAsync(t, email.Trim().ToLowerInvariant(), "signup");
        var response = await VerifyAsync(t, email, code);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("registrationToken").GetString()!;
    }

    private static string WrongCode(string code) => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    private async Task DeleteCooldownAsync(string email) =>
        await containers.Redis.GetDatabase().KeyDeleteAsync($"code:cooldown:{email}:signup");

    private static async Task<Account?> AccountAsync(TestIdentity t, string normalizedEmail)
    {
        await using var db = t.NewDb();
        return await db.Accounts.SingleOrDefaultAsync(a => a.EmailNormalized == normalizedEmail, Ct);
    }

    [Fact]
    public async Task Sign_up_creates_account_and_session()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var email = NewEmail();

        var start = await StartAsync(t, email);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var envelope = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode));
        var sent = envelope.GetPayload<VerificationCodeV1>();
        Assert.Equal(email, sent.Email);
        Assert.Equal("signup", sent.Purpose);
        Assert.Equal(t.Clock.GetUtcNow().AddMinutes(15), sent.ExpiresAt);
        Assert.False(string.IsNullOrEmpty(sent.EncryptedCode));

        var code = await Outbox.LatestCodeAsync(t, email, "signup");
        var verify = await VerifyAsync(t, email, code);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var token = (await verify.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("registrationToken").GetString()!;

        var complete = await CompleteAsync(t, token);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.False(string.IsNullOrEmpty((await complete.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(complete.RefreshCookie()));

        var account = await AccountAsync(t, email);
        Assert.NotNull(account);
        Assert.Equal(AccountTypes.Customer, account.Type);
        Assert.Equal(AccountStatuses.Active, account.Status);
        Assert.Equal("Ann Lee", account.Name);
        Assert.StartsWith("$argon2id$", account.PasswordHash);
        var registered = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.CustomerRegistered)).GetPayload<CustomerRegisteredV1>();
        Assert.Equal(new CustomerRegisteredV1(account.Id, email, "Ann Lee"), registered);
        Assert.Equal(1, metrics.Sum("codes_sent_total", ("purpose", "signup")));
    }

    [Fact]
    public async Task ID_T01_start_with_staff_email_is_EMAIL_ALREADY_REGISTERED()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var staff = (await Seed.StaffAsync(t)).Account;

        foreach (var typed in new[] { staff.EmailNormalized, staff.EmailNormalized.ToUpperInvariant(), $" {staff.EmailNormalized.ToUpperInvariant()} " })
            await (await StartAsync(t, typed)).ExpectErrorAsync(409, "EMAIL_ALREADY_REGISTERED");

        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode));
    }

    [Fact]
    public async Task ID_T01b_tampered_or_31_minute_old_token_is_invalid()
    {
        await using var t = await TestIdentity.StartAsync(containers);

        var token = await RegistrationTokenAsync(t, NewEmail());
        var middle = token.Length / 2;
        var tampered = token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..];
        await (await CompleteAsync(t, tampered)).ExpectErrorAsync(400, "REGISTRATION_TOKEN_INVALID");
        await (await CompleteAsync(t, "not-a-token")).ExpectErrorAsync(400, "REGISTRATION_TOKEN_INVALID");

        t.Clock.Advance(TimeSpan.FromMinutes(31));
        await (await CompleteAsync(t, token)).ExpectErrorAsync(400, "REGISTRATION_TOKEN_INVALID");

        var fresh = await RegistrationTokenAsync(t, NewEmail());
        t.Clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(t, fresh)).StatusCode);
    }

    [Fact]
    public async Task ID_T01c_email_field_in_request_is_ignored()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var email = NewEmail();
        var other = NewEmail();
        var token = await RegistrationTokenAsync(t, email);

        var response = await CompleteAsync(t, token, body: new { registrationToken = token, password = Seed.Password, name = "Ann Lee", email = other });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await AccountAsync(t, email));
        Assert.Null(await AccountAsync(t, other));
    }

    [Fact]
    public async Task ID_T01d_outbox_holds_ciphertext_only()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var email = NewEmail();
        await StartAsync(t, email);
        var code = await Outbox.LatestCodeAsync(t, email, "signup");

        var envelope = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode));
        Assert.Equal(["email", "encryptedCode", "expiresAt", "purpose"], envelope.Payload.EnumerateObject().Select(p => p.Name).Order());
        var sealedCode = Base64Url.DecodeFromChars(envelope.Payload.GetProperty("encryptedCode").GetString()!);
        Assert.Equal(34, sealedCode.Length); // nonce 12 + 6 digits + tag 16
        Assert.Equal(code, Encoding.UTF8.GetString(new SecretBox(t.Secrets.CodeEncryptionKey).Open(sealedCode)));
        Assert.DoesNotContain($"\"{code}\"", envelope.Payload.GetRawText());

        await using var db = t.NewDb();
        var row = await db.VerificationCodes.SingleAsync(c => c.TargetEmail == email, Ct);
        Assert.NotEqual(Encoding.UTF8.GetBytes(code), row.CodeHmac);
    }

    [Fact]
    public async Task ID_T10_sixth_code_attempt_is_CODE_INVALID_even_if_right()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var email = NewEmail();
        await StartAsync(t, email);
        var code = await Outbox.LatestCodeAsync(t, email, "signup");

        for (var i = 0; i < 5; i++) await (await VerifyAsync(t, email, WrongCode(code))).ExpectErrorAsync(400, "CODE_INVALID");
        await (await VerifyAsync(t, email, code)).ExpectErrorAsync(400, "CODE_INVALID");
    }

    [Fact]
    public async Task Expired_or_consumed_code_is_CODE_INVALID()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var expired = NewEmail();
        await StartAsync(t, expired);
        var expiredCode = await Outbox.LatestCodeAsync(t, expired, "signup");
        t.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await (await VerifyAsync(t, expired, expiredCode)).ExpectErrorAsync(400, "CODE_INVALID");

        var email = NewEmail();
        await RegistrationTokenAsync(t, email);
        await (await VerifyAsync(t, email, await Outbox.LatestCodeAsync(t, email, "signup"))).ExpectErrorAsync(400, "CODE_INVALID");
    }

    [Fact]
    public async Task New_code_invalidates_the_previous_one()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var email = NewEmail();
        await StartAsync(t, email);
        var oldCode = await Outbox.LatestCodeAsync(t, email, "signup");
        await DeleteCooldownAsync(email);
        await StartAsync(t, email);
        var newCode = await Outbox.LatestCodeAsync(t, email, "signup");
        Assert.SkipWhen(oldCode == newCode, "the two random codes collided");

        await (await VerifyAsync(t, email, oldCode)).ExpectErrorAsync(400, "CODE_INVALID");
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(t, email, newCode)).StatusCode);
    }

    [Fact]
    public async Task Resend_cooldown_and_hourly_cap()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var email = NewEmail();

        Assert.Equal(HttpStatusCode.OK, (await StartAsync(t, email)).StatusCode);
        await (await StartAsync(t, email)).ExpectErrorAsync(429, "CODE_RATE_LIMITED");
        for (var i = 2; i <= 5; i++)
        {
            await DeleteCooldownAsync(email);
            Assert.Equal(HttpStatusCode.OK, (await StartAsync(t, email)).StatusCode);
        }
        await DeleteCooldownAsync(email);
        await (await StartAsync(t, email)).ExpectErrorAsync(429, "CODE_RATE_LIMITED");
        Assert.Equal(5, (await Outbox.EnvelopesAsync(t, IdentityMessages.VerificationCode)).Count);
    }

    [Fact]
    public async Task Registration_token_used_twice_is_EMAIL_TAKEN()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var token = await RegistrationTokenAsync(t, NewEmail());

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(t, token)).StatusCode);
        await (await CompleteAsync(t, token)).ExpectErrorAsync(409, "EMAIL_TAKEN");
        Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.CustomerRegistered));
    }

    [Fact]
    public async Task Weak_password_and_bad_name_are_rejected()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var email = NewEmail();
        var token = await RegistrationTokenAsync(t, email);

        await (await CompleteAsync(t, token, password: "short")).ExpectErrorAsync(400, "PASSWORD_WEAK");
        foreach (var name in new[] { "", new string('x', 101) })
        {
            var info = await (await CompleteAsync(t, token, name: name)).ExpectErrorAsync(400, "VALIDATION_FAILED");
            Assert.Equal("name", info.GetProperty("metadata").GetProperty("field").GetString());
        }
        Assert.Null(await AccountAsync(t, email));
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(t, token)).StatusCode);
    }

    [Fact]
    public async Task Mixed_case_email_is_normalized()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var local = $"Ann{Guid.NewGuid():N}";
        var display = $"{local}@Example.COM";
        var normalized = display.ToLowerInvariant();

        var token = await RegistrationTokenAsync(t, $" {display} ");
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(t, token)).StatusCode);

        var account = await AccountAsync(t, normalized);
        Assert.NotNull(account);
        Assert.Equal(display, account.EmailDisplay);
        Assert.Equal(display, Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.CustomerRegistered)).GetPayload<CustomerRegisteredV1>().Email);
        await (await StartAsync(t, $"{local.ToUpperInvariant()}@example.com")).ExpectErrorAsync(409, "EMAIL_ALREADY_REGISTERED");
    }
}
