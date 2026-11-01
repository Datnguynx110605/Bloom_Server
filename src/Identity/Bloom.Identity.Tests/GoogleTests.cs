using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Identity.Tests;

public class GoogleTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    private static string NewSub() => Guid.NewGuid().ToString("N");

    private Task<TestIdentity> StartAsync(FakeGoogle google) => TestIdentity.StartAsync(containers, o =>
    {
        o.Config["Identity:Google:JwksUrl"] = google.JwksUrl.ToString();
        o.Config["Identity:Google:ClientId"] = google.ClientId;
    });

    private static Task<HttpResponseMessage> SignInAsync(TestIdentity t, string idToken) =>
        t.SendAsync(HttpMethod.Post, "/api/auth/google", new { idToken });

    private static Task<HttpResponseMessage> LinkAsync(TestIdentity t, TestCaller caller, string idToken) =>
        t.SendAsync(HttpMethod.Post, "/api/me/google", new { idToken }, caller);

    private static Task<HttpResponseMessage> UnlinkAsync(TestIdentity t, TestCaller caller) =>
        t.SendAsync(HttpMethod.Delete, "/api/me/google", caller: caller);

    private static TestCaller Recent(TestIdentity t, Account a) => TestCaller.Customer(a.Id, t.Clock.GetUtcNow());

    private static async Task<Account> StoredAsync(TestIdentity t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Accounts.SingleAsync(a => a.Id == id, Ct);
    }

    private static async Task ExpectTokensAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(response.RefreshCookie()));
    }

    [Fact]
    public async Task New_google_user_becomes_a_customer()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        using var metrics = new MetricCapture(t.Services);
        var (sub, email) = (NewSub(), $"  {Guid.NewGuid():N}@Example.com ");

        var response = await SignInAsync(t, google.Mint(sub, email));

        await ExpectTokensAsync(response);
        await using var db = t.NewDb();
        var account = await db.Accounts.SingleAsync(a => a.GoogleSub == sub, Ct);
        Assert.Equal(AccountTypes.Customer, account.Type);
        Assert.Null(account.PasswordHash);
        Assert.Equal("Ann Lee", account.Name);
        Assert.Equal(email.Trim(), account.EmailDisplay);
        Assert.Equal(email.Trim().ToLowerInvariant(), account.EmailNormalized);
        Assert.Equal(t.Clock.GetUtcNow(), account.LastSignInAt!.Value, TimeSpan.FromMilliseconds(1));
        var registered = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.CustomerRegistered)).GetPayload<CustomerRegisteredV1>();
        Assert.Equal(new CustomerRegisteredV1(account.Id, email.Trim(), "Ann Lee"), registered);
        Assert.Equal(1, metrics.Sum("signins_total", ("type", "customer"), ("method", "google"), ("result", "success")));
    }

    [Fact]
    public async Task New_google_user_without_a_name_gets_the_email_local_part()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        var sub = NewSub();

        await ExpectTokensAsync(await SignInAsync(t, google.Mint(sub, "ann.lee@example.com", name: null)));

        await using var db = t.NewDb();
        Assert.Equal("ann.lee", (await db.Accounts.SingleAsync(a => a.GoogleSub == sub, Ct)).Name);
    }

    [Fact]
    public async Task Known_google_sub_signs_in()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        var sub = NewSub();
        var account = await Seed.CustomerAsync(t, googleSub: sub);

        // The Google email is not the account's email: the sub decides.
        await ExpectTokensAsync(await SignInAsync(t, google.Mint(sub, NewEmail())));

        Assert.Equal(t.Clock.GetUtcNow(), (await StoredAsync(t, account.Id)).LastSignInAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.CustomerRegistered));
    }

    [Fact]
    public async Task ID_T08_google_email_of_existing_customer_links()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        var account = await Seed.CustomerAsync(t);
        var sub = NewSub();

        await ExpectTokensAsync(await SignInAsync(t, google.Mint(sub, $" {account.EmailNormalized.ToUpperInvariant()} ")));

        var stored = await StoredAsync(t, account.Id);
        Assert.Equal(sub, stored.GoogleSub);
        Assert.NotNull(stored.PasswordHash);
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.CustomerRegistered));
    }

    [Fact]
    public async Task ID_T09_google_email_of_staff_is_GOOGLE_NOT_ALLOWED()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        using var metrics = new MetricCapture(t.Services);
        var (staff, _) = await Seed.StaffAsync(t);
        var sub = NewSub();

        var response = await SignInAsync(t, google.Mint(sub, staff.EmailNormalized));

        await response.ExpectErrorAsync(400, "GOOGLE_NOT_ALLOWED");
        Assert.Null(response.RefreshCookie());
        await using var db = t.NewDb();
        Assert.Empty(await db.RefreshTokens.Where(r => r.AccountId == staff.Id).ToListAsync(Ct));
        Assert.Null((await StoredAsync(t, staff.Id)).GoogleSub);
        Assert.Equal(1, metrics.Sum("signins_total", ("type", "staff"), ("method", "google"), ("result", "not_allowed")));
    }

    [Theory]
    [InlineData("aud")]
    [InlineData("iss")]
    [InlineData("exp")]
    [InlineData("email_verified")]
    [InlineData("signature")]
    public async Task Bad_google_token_is_INVALID_CREDENTIALS(string defect)
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        using var metrics = new MetricCapture(t.Services);
        var email = NewEmail();
        var token = defect switch
        {
            "aud" => google.Mint(NewSub(), email, aud: "someone-else"),
            "iss" => google.Mint(NewSub(), email, iss: "https://evil.example"),
            "exp" => google.Mint(NewSub(), email, exp: t.Clock.GetUtcNow().AddMinutes(-1)),
            "email_verified" => google.Mint(NewSub(), email, emailVerified: false),
            _ => google.Mint(NewSub(), email).Split('.') is [var h, var p, _] ? $"{h}.{p}.{Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(256))}" : "",
        };

        var response = await SignInAsync(t, token);

        await response.ExpectErrorAsync(401, "INVALID_CREDENTIALS");
        Assert.Null(response.RefreshCookie());
        await using var db = t.NewDb();
        Assert.False(await db.Accounts.AnyAsync(a => a.EmailNormalized == email, Ct));
        Assert.Equal(1, metrics.Sum("signins_total", ("type", "unknown"), ("method", "google"), ("result", "invalid")));
    }

    [Fact]
    public async Task Expiry_is_checked_on_the_app_clock()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        var token = google.Mint(NewSub(), NewEmail());

        t.Clock.Advance(TimeSpan.FromHours(2)); // the token is valid by the system clock, expired by the app's

        var response = await SignInAsync(t, token);
        await response.ExpectErrorAsync(401, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Garbage_token_is_INVALID_CREDENTIALS()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);

        foreach (var junk in new[] { "", "not-a-jwt", "a.b.c" })
            await (await SignInAsync(t, junk)).ExpectErrorAsync(401, "INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task Unknown_kid_refetches_jwks_once()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);

        await ExpectTokensAsync(await SignInAsync(t, google.Mint(NewSub(), NewEmail())));
        Assert.Equal(1, google.JwksRequests);

        google.RotateKey();
        await ExpectTokensAsync(await SignInAsync(t, google.Mint(NewSub(), NewEmail())));
        Assert.Equal(2, google.JwksRequests);

        await ExpectTokensAsync(await SignInAsync(t, google.Mint(NewSub(), NewEmail())));
        Assert.Equal(2, google.JwksRequests);
    }

    [Fact]
    public async Task Link_google_rules()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        var account = await Seed.CustomerAsync(t);
        var (sub, other) = (NewSub(), NewSub());

        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(t, Recent(t, account), google.Mint(sub, NewEmail()))).StatusCode);
        Assert.Equal(sub, (await StoredAsync(t, account.Id)).GoogleSub);

        // A sub on another account.
        var second = await Seed.CustomerAsync(t);
        await (await LinkAsync(t, Recent(t, second), google.Mint(sub, NewEmail()))).ExpectErrorAsync(409, "GOOGLE_ALREADY_LINKED");
        Assert.Null((await StoredAsync(t, second.Id)).GoogleSub);

        // Linking again replaces the caller's own sub (plan I19).
        Assert.Equal(HttpStatusCode.OK, (await LinkAsync(t, Recent(t, account), google.Mint(other, NewEmail()))).StatusCode);
        Assert.Equal(other, (await StoredAsync(t, account.Id)).GoogleSub);

        await (await LinkAsync(t, Recent(t, account), google.Mint(NewSub(), NewEmail(), emailVerified: false))).ExpectErrorAsync(401, "INVALID_CREDENTIALS");

        var (staff, _) = await Seed.StaffAsync(t);
        await (await LinkAsync(t, TestCaller.Staff(staff.Id, "support", t.Clock.GetUtcNow()), google.Mint(NewSub(), NewEmail())))
            .ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }

    [Fact]
    public async Task Unlink_google_rules()
    {
        await using var google = await FakeGoogle.StartAsync();
        await using var t = await StartAsync(google);
        var googleOnly = await Seed.CustomerAsync(t, password: null, googleSub: NewSub());
        var withPassword = await Seed.CustomerAsync(t, googleSub: NewSub());

        await (await UnlinkAsync(t, TestCaller.Customer(withPassword.Id, t.Clock.GetUtcNow().AddMinutes(-10)))).ExpectErrorAsync(400, "REAUTH_REQUIRED");
        Assert.NotNull((await StoredAsync(t, withPassword.Id)).GoogleSub);

        await (await UnlinkAsync(t, Recent(t, googleOnly))).ExpectErrorAsync(400, "PASSWORD_REQUIRED");
        Assert.NotNull((await StoredAsync(t, googleOnly.Id)).GoogleSub);

        Assert.Equal(HttpStatusCode.OK, (await UnlinkAsync(t, Recent(t, withPassword))).StatusCode);
        Assert.Null((await StoredAsync(t, withPassword.Id)).GoogleSub);

        var (staff, _) = await Seed.StaffAsync(t);
        await (await UnlinkAsync(t, TestCaller.Staff(staff.Id, "support", t.Clock.GetUtcNow()))).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");
    }
}
