using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Tests;

public class DeletionTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestIdentity> StartAsync(Containers c, FakeOrder order) =>
        TestIdentity.StartAsync(c, o => o.Config["Identity:OrderUrl"] = order.Address.ToString());

    private static Task<HttpResponseMessage> DeleteAsync(TestIdentity t, TestCaller caller) =>
        t.SendAsync(HttpMethod.Delete, "/api/me/account", caller: caller);

    /// <summary>Two addresses, one code by account id and one by target email only, and two sessions.</summary>
    private static async Task SeedPersonalDataAsync(TestIdentity t, Account a)
    {
        await using var db = t.NewDb();
        var now = t.Clock.GetUtcNow();
        for (var i = 0; i < 2; i++)
            db.Addresses.Add(new Address { Id = Guid.CreateVersion7(), AccountId = a.Id, Recipient = "Ann", Line1 = "1 Main St", City = "Austin", State = "TX", Zip = "78701" });
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = Guid.CreateVersion7(), Purpose = CodePurposes.ChangeEmail, TargetEmail = "next@example.com", AccountId = a.Id, CodeHmac = [1], ExpiresAt = now.AddMinutes(15), CreatedAt = now,
        });
        db.VerificationCodes.Add(new VerificationCode
        {
            Id = Guid.CreateVersion7(), Purpose = CodePurposes.ResetPassword, TargetEmail = a.EmailNormalized, CodeHmac = [2], ExpiresAt = now.AddMinutes(15), CreatedAt = now,
        });
        await db.SaveChangesAsync(Ct);
        await Seed.SessionAsync(t, a);
        await Seed.SessionAsync(t, a);
    }

    private static async Task AssertUntouchedAsync(TestIdentity t, Account before)
    {
        var stored = await t.AccountAsync(before.Id);
        Assert.Equal(AccountStatuses.Active, stored.Status);
        Assert.Equal(before.EmailNormalized, stored.EmailNormalized);
        Assert.Equal(before.PasswordHash, stored.PasswordHash);
        await using var db = t.NewDb();
        Assert.Equal(2, await db.Addresses.CountAsync(a => a.AccountId == before.Id, Ct));
        Assert.Equal(2, await db.VerificationCodes.CountAsync(c => c.AccountId == before.Id || c.TargetEmail == before.EmailNormalized, Ct));
        Assert.Equal(2, await db.RefreshTokens.CountAsync(r => r.AccountId == before.Id && r.Status == RefreshStatuses.Active, Ct));
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.AccountDeleted));
        Assert.Empty(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
    }

    [Fact]
    public async Task ID_T13_delete_with_unsettled_orders_is_refused()
    {
        await using var order = await FakeOrder.StartAsync();
        order.Unsettled = true;
        await using var t = await StartAsync(containers, order);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        await SeedPersonalDataAsync(t, account);

        await (await DeleteAsync(t, TestCaller.Customer(t, account))).ExpectErrorAsync(400, "UNSETTLED_ORDERS");

        Assert.Equal(account.Id, order.LastAccountId);
        await AssertUntouchedAsync(t, account);
        Assert.Equal(1, metrics.Sum("account_deletions_total", ("result", "unsettled_orders")));
        Assert.Equal(0, metrics.Sum("account_deletions_total", ("result", "deleted")));
    }

    [Fact]
    public async Task ID_T14_delete_clears_personal_data_and_frees_email()
    {
        await using var order = await FakeOrder.StartAsync();
        await using var t = await StartAsync(containers, order);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t, googleSub: $"sub-{Guid.NewGuid():N}");
        await SeedPersonalDataAsync(t, account);

        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(t, TestCaller.Customer(t, account))).StatusCode);

        Assert.Equal(account.Id, order.LastAccountId);
        var stored = await t.AccountAsync(account.Id);
        Assert.Equal(AccountStatuses.Deleted, stored.Status);
        Assert.Equal($"deleted+{account.Id:D}@invalid", stored.EmailNormalized);
        Assert.Null(stored.EmailDisplay);
        Assert.Null(stored.Name);
        Assert.Null(stored.PasswordHash);
        Assert.Null(stored.GoogleSub);
        Assert.NotNull(stored.TokensValidAfter);
        await using (var db = t.NewDb())
        {
            Assert.False(await db.Addresses.AnyAsync(a => a.AccountId == account.Id, Ct));
            Assert.False(await db.VerificationCodes.AnyAsync(c => c.AccountId == account.Id || c.TargetEmail == account.EmailNormalized, Ct));
            var tokens = await db.RefreshTokens.Where(r => r.AccountId == account.Id).ToListAsync(Ct);
            Assert.Equal(2, tokens.Count);
            Assert.All(tokens, r => Assert.Equal(RefreshStatuses.Revoked, r.Status));
        }

        var deleted = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.AccountDeleted));
        Assert.Equal(account.Id, deleted.GetPayload<AccountDeletedV1>().AccountId);
        var revoked = Assert.Single(await Outbox.EnvelopesAsync(t, IdentityMessages.TokensRevoked));
        Assert.Equal(account.Id, revoked.GetPayload<TokensRevokedV1>().AccountId);
        Assert.Equal(1, metrics.Sum("account_deletions_total", ("result", "deleted")));
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "account_deleted")));
    }

    [Fact]
    public async Task Order_down_is_ORDER_SERVICE_UNAVAILABLE_and_changes_nothing()
    {
        await using var order = await FakeOrder.StartAsync();
        await using var t = await StartAsync(containers, order);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        await SeedPersonalDataAsync(t, account);

        order.Fail = true;
        await (await DeleteAsync(t, TestCaller.Customer(t, account))).ExpectErrorAsync(503, "ORDER_SERVICE_UNAVAILABLE");
        await AssertUntouchedAsync(t, account);

        await order.DisposeAsync(); // nothing listens any more
        await (await DeleteAsync(t, TestCaller.Customer(t, account))).ExpectErrorAsync(503, "ORDER_SERVICE_UNAVAILABLE");
        await AssertUntouchedAsync(t, account);
        Assert.Equal(2, metrics.Sum("account_deletions_total", ("result", "order_unavailable")));
    }

    [Fact]
    public async Task Slow_order_hits_the_2s_deadline()
    {
        await using var order = await FakeOrder.StartAsync();
        order.Delay = TimeSpan.FromSeconds(5);
        await using var t = await StartAsync(containers, order);
        var account = await Seed.CustomerAsync(t);

        var started = DateTime.UtcNow;
        await (await DeleteAsync(t, TestCaller.Customer(t, account))).ExpectErrorAsync(503, "ORDER_SERVICE_UNAVAILABLE");

        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));
        Assert.Equal(AccountStatuses.Active, (await t.AccountAsync(account.Id)).Status);
    }

    [Fact]
    public async Task Deletion_needs_reauth_and_a_customer()
    {
        await using var order = await FakeOrder.StartAsync();
        await using var t = await StartAsync(containers, order);
        var account = await Seed.CustomerAsync(t);
        var (staff, _) = await Seed.StaffAsync(t);

        await (await DeleteAsync(t, TestCaller.Customer(account.Id, t.Clock.GetUtcNow().AddMinutes(-10)))).ExpectErrorAsync(400, "REAUTH_REQUIRED");
        await (await DeleteAsync(t, TestCaller.Staff(staff.Id, "support", t.Clock.GetUtcNow()))).ExpectErrorAsync(403, "ROLE_NOT_ALLOWED");

        Assert.Null(order.LastAccountId);
        Assert.Equal(AccountStatuses.Active, (await t.AccountAsync(account.Id)).Status);
        Assert.Equal(AccountStatuses.Active, (await t.AccountAsync(staff.Id)).Status);
    }

    [Fact]
    public async Task Deleted_customers_email_can_sign_up_again()
    {
        await using var order = await FakeOrder.StartAsync();
        await using var t = await StartAsync(containers, order);
        var email = $"{Guid.NewGuid():N}@example.com";
        var account = await Seed.CustomerAsync(t, email);
        Assert.Equal(HttpStatusCode.OK, (await DeleteAsync(t, TestCaller.Customer(t, account))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/start", new { email })).StatusCode);
        var code = await Outbox.LatestCodeAsync(t, email, "signup");
        var verify = await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/verify", new { email, code });
        var token = (await verify.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("registrationToken").GetString();
        var complete = await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/complete", new { registrationToken = token, password = Seed.Password, name = "Ann Lee" });

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        await using var db = t.NewDb();
        var again = await db.Accounts.SingleAsync(a => a.EmailNormalized == email, Ct);
        Assert.NotEqual(account.Id, again.Id);
        Assert.Equal(AccountStatuses.Active, again.Status);
    }
}
