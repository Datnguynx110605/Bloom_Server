using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Identity.Tests;

public class SessionTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> RefreshAsync(TestIdentity t, string? token) =>
        t.SendAsync(HttpMethod.Post, "/auth/refresh", cookie: token is null ? null : $"bloom_rt={token}");

    private static async Task<List<RefreshToken>> RowsAsync(TestIdentity t, Guid accountId)
    {
        await using var db = t.NewDb();
        return await db.RefreshTokens.Where(r => r.AccountId == accountId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(Ct);
    }

    private static async Task<RefreshToken> RowAsync(TestIdentity t, string rawToken)
    {
        await using var db = t.NewDb();
        var hash = Opaque.Sha256(rawToken);
        return await db.RefreshTokens.SingleAsync(r => r.TokenHash == hash, Ct);
    }

    private static JsonElement Payload(string token)
    {
        var parts = token.Split('.');
        return JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1])).RootElement.Clone();
    }

    private static async Task<string> AccessTokenAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;

    private static void AssertNoStore(HttpResponseMessage r) => Assert.True(r.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");

    [Fact]
    public async Task Access_token_has_exactly_the_LLD_claims()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var customer = await Seed.CustomerAsync(t);
        var staff = (await Seed.StaffAsync(t, StaffRoles.Warehouse)).Account;

        foreach (var (account, type) in new[] { (customer, "customer"), (staff, "staff") })
        {
            var before = await t.DbNowAsync();
            var session = await Seed.SessionAsync(t, account);
            var after = await t.DbNowAsync();
            var p = Payload(session.AccessToken);
            var expected = new[] { "iss", "aud", "sub", "typ", "auth_time", "iat", "exp", "jti", "sid" }.Concat(type == "staff" ? ["role"] : []);

            Assert.Equal(expected.Order(), p.EnumerateObject().Select(x => x.Name).Order());
            Assert.Equal("bloom-identity", p.GetProperty("iss").GetString());
            Assert.Equal("bloom", p.GetProperty("aud").GetString());
            Assert.Equal(account.Id.ToString(), p.GetProperty("sub").GetString());
            Assert.Equal(type, p.GetProperty("typ").GetString());
            Assert.Equal(900, p.GetProperty("exp").GetInt64() - p.GetProperty("iat").GetInt64());
            Assert.InRange(p.GetProperty("iat").GetInt64(), before.ToUnixTimeSeconds(), after.ToUnixTimeSeconds()); // the database's now()
            Assert.InRange(p.GetProperty("auth_time").GetInt64(), before.ToUnixTimeSeconds(), after.ToUnixTimeSeconds());
            Assert.Equal((await RowAsync(t, session.RefreshToken)).FamilyId.ToString(), p.GetProperty("sid").GetString());
            if (type == "staff") Assert.Equal("warehouse", p.GetProperty("role").GetString());
        }
    }

    [Fact]
    public async Task Jwks_lists_every_key_and_validates_identity_tokens()
    {
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var t = await TestIdentity.StartAsync(containers, o =>
        {
            o.Config["Identity:SigningKeys:1:Kid"] = "test-key-2";
            o.Config["Identity:SigningKeys:1:PrivateKeyPem"] = second.ExportPkcs8PrivateKeyPem();
            o.Config["Identity:ActiveKeyId"] = "test-key-2";
        });
        var session = await Seed.SessionAsync(t, await Seed.CustomerAsync(t));

        var response = await t.Http.GetAsync("/.well-known/jwks.json", Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var keys = JsonDocument.Parse(json).RootElement.GetProperty("keys").EnumerateArray().ToList();
        Assert.Equal(["test-key-1", "test-key-2"], keys.Select(k => k.GetProperty("kid").GetString()!).Order());
        foreach (var k in keys)
        {
            Assert.Equal("EC", k.GetProperty("kty").GetString());
            Assert.Equal("P-256", k.GetProperty("crv").GetString());
            Assert.Equal("sig", k.GetProperty("use").GetString());
            Assert.Equal("ES256", k.GetProperty("alg").GetString());
            Assert.False(string.IsNullOrEmpty(k.GetProperty("x").GetString()));
            Assert.False(string.IsNullOrEmpty(k.GetProperty("y").GetString()));
            Assert.False(k.TryGetProperty("d", out _));
        }

        var jwt = new JsonWebToken(session.AccessToken);
        Assert.Equal("test-key-2", jwt.Kid);
        Assert.Equal("ES256", jwt.Alg);
        Assert.Equal("JWT", jwt.Typ);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(session.AccessToken, new TokenValidationParameters // the gateway's (TokenValidator)
        {
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidIssuer = "bloom-identity",
            ValidAudience = "bloom",
            IssuerSigningKeys = new JsonWebKeySet(json).GetSigningKeys(),
            RequireSignedTokens = true,
        });
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public async Task ID_T05_refresh_rotates_token_and_cookie()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        var session = await Seed.SessionAsync(t, account);
        await using (var db = t.NewDb()) // the sign-in was 20 minutes ago, so auth_time must differ from the new token's iat
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE refresh_tokens SET auth_time = now() - interval '20 minutes'", Ct);
        var authTime = (await RowAsync(t, session.RefreshToken)).AuthTime.ToUnixTimeSeconds();

        var before = await t.DbNowAsync();
        var response = await RefreshAsync(t, session.RefreshToken);
        var after = await t.DbNowAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        var newToken = response.RefreshCookie();
        Assert.False(string.IsNullOrEmpty(newToken));
        Assert.NotEqual(session.RefreshToken, newToken);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("bloom_rt=")).ToLowerInvariant();
        foreach (var attribute in new[] { "secure", "httponly", "samesite=strict", "path=/auth/refresh", "expires=" })
            Assert.Contains(attribute, setCookie);

        var old = await RowAsync(t, session.RefreshToken);
        var current = await RowAsync(t, newToken);
        Assert.Equal(RefreshStatuses.Rotated, old.Status);
        Assert.InRange(old.UsedAt!.Value, before, after);
        Assert.Equal(RefreshStatuses.Active, current.Status);
        Assert.Equal(old.FamilyId, current.FamilyId);
        var payload = Payload(await AccessTokenAsync(response));
        Assert.Equal(authTime, payload.GetProperty("auth_time").GetInt64());
        Assert.InRange(payload.GetProperty("iat").GetInt64(), before.ToUnixTimeSeconds(), after.ToUnixTimeSeconds());
        Assert.Equal(1, metrics.Sum("refresh_total", ("result", "rotated")));
    }

    [Fact]
    public async Task Customer_refresh_slides_seven_days()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var session = await Seed.SessionAsync(t, await Seed.CustomerAsync(t));
        await using (var db = t.NewDb()) // six of the seven days have passed
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE refresh_tokens SET expires_at = now() + interval '1 day'", Ct);

        var before = await t.DbNowAsync();
        var response = await RefreshAsync(t, session.RefreshToken);
        var after = await t.DbNowAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange((await RowAsync(t, response.RefreshCookie()!)).ExpiresAt, before + TimeSpan.FromDays(7), after + TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task ID_T06_reuse_of_rotated_token_revokes_all_sessions()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        var a = await Seed.SessionAsync(t, account);
        await Seed.SessionAsync(t, account);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(t, a.RefreshToken)).StatusCode);

        var before = await t.DbNowAsync();
        var replay = await RefreshAsync(t, a.RefreshToken);
        var after = await t.DbNowAsync();

        await replay.ExpectErrorAsync(401, "REFRESH_INVALID");
        AssertNoStore(replay);
        Assert.All(await RowsAsync(t, account.Id), r => Assert.Equal(RefreshStatuses.Revoked, r.Status));
        await using var db = t.NewDb();
        var stored = await db.Accounts.SingleAsync(x => x.Id == account.Id, Ct);
        Assert.InRange(stored.TokensValidAfter!.Value, before, after);
        var envelope = Assert.Single(await Outbox.EnvelopesAsync(t, "identity.tokens-revoked.v1"));
        var payload = envelope.GetPayload<TokensRevokedV1>();
        Assert.Equal(account.Id, payload.AccountId);
        Assert.InRange(payload.IssuedBefore, before.ToUnixTimeMilliseconds(), after.ToUnixTimeMilliseconds());
        Assert.Equal(1, metrics.Sum("refresh_total", ("result", "reuse")));
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "refresh_reuse")));
    }

    [Fact]
    public async Task ID_T17_revoked_refresh_after_sign_out_everywhere_is_invalid_without_side_effects() // D8, LLD-ADR-013
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        var oldDevice = await Seed.SessionAsync(t, account);
        await Seed.SessionAsync(t, account);
        var caller = TestCaller.Customer(account.Id, t.Clock.GetUtcNow());
        Assert.Equal(HttpStatusCode.OK, (await t.SendAsync(HttpMethod.Post, "/api/me/sign-out-everywhere", caller: caller)).StatusCode);
        var current = await Seed.SessionAsync(t, account);

        var stale = await RefreshAsync(t, oldDevice.RefreshToken);

        await stale.ExpectErrorAsync(401, "REFRESH_INVALID");
        Assert.Equal(RefreshStatuses.Active, (await RowAsync(t, current.RefreshToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(t, current.RefreshToken)).StatusCode);
        Assert.Equal(0, metrics.Sum("refresh_total", ("result", "reuse")));
        Assert.Equal(1, metrics.Sum("refresh_total", ("result", "invalid")));
        Assert.Equal(1, metrics.Sum("revocations_total"));
        Assert.Single(await Outbox.EnvelopesAsync(t, "identity.tokens-revoked.v1")); // the sign-out's, not a reuse revoke-all
    }

    [Fact]
    public async Task ID_T07_staff_family_expires_after_12_hours()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var session = await Seed.SessionAsync(t, (await Seed.StaffAsync(t)).Account);
        await using var db = t.NewDb(); // 11 h 59 min of the 12 h have passed
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE refresh_tokens SET expires_at = now() + interval '1 minute'", Ct);
        var familyExpiry = (await RowAsync(t, session.RefreshToken)).ExpiresAt;

        var ok = await RefreshAsync(t, session.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(familyExpiry, (await RowAsync(t, ok.RefreshCookie()!)).ExpiresAt); // the family's absolute expiry carries over
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE refresh_tokens SET expires_at = now() - interval '1 minute'", Ct); // and now it has passed
        var expired = await RefreshAsync(t, ok.RefreshCookie());

        await expired.ExpectErrorAsync(401, "REFRESH_INVALID");
        Assert.Empty(await Outbox.EnvelopesAsync(t, "identity.tokens-revoked.v1"));
    }

    [Fact]
    public async Task Refresh_expiry_follows_the_database_clock_not_the_app_clock() // 00-reliability §10
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var session = await Seed.SessionAsync(t, await Seed.CustomerAsync(t));
        t.Clock.Advance(TimeSpan.FromDays(30)); // past the 7 days on the app clock; the database has not moved

        var before = await t.DbNowAsync();
        var response = await RefreshAsync(t, session.RefreshToken);
        var after = await t.DbNowAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange((await RowAsync(t, response.RefreshCookie()!)).ExpiresAt, before + TimeSpan.FromDays(7), after + TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task Missing_or_unknown_cookie_is_REFRESH_INVALID_and_no_store()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);

        foreach (var token in new[] { null, Opaque.NewToken() })
        {
            var response = await RefreshAsync(t, token);
            await response.ExpectErrorAsync(401, "REFRESH_INVALID");
            AssertNoStore(response);
        }
        Assert.Equal(2, metrics.Sum("refresh_total", ("result", "invalid")));
    }

    [Fact]
    public async Task Concurrent_refresh_of_one_token_counts_as_reuse() // RF3
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        var session = await Seed.SessionAsync(t, account);

        var responses = await Task.WhenAll(RefreshAsync(t, session.RefreshToken), RefreshAsync(t, session.RefreshToken));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized], responses.Select(r => r.StatusCode).Order());
        await responses.Single(r => r.StatusCode == HttpStatusCode.Unauthorized).ExpectErrorAsync(401, "REFRESH_INVALID");
        Assert.All(await RowsAsync(t, account.Id), r => Assert.Equal(RefreshStatuses.Revoked, r.Status));
        Assert.Single(await Outbox.EnvelopesAsync(t, "identity.tokens-revoked.v1"));
    }

    [Fact]
    public async Task SignOutEverywhere_waits_for_a_rotation_in_flight_and_revokes_its_new_token() // R41
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        var session = await Seed.SessionAsync(t, account);
        var hash = Opaque.Sha256(session.RefreshToken);

        // A rotation that has not committed yet, in the service's lock order: account row, old token, then the new token.
        await using var rotating = t.NewDb();
        await using var tx = await rotating.Database.BeginTransactionAsync(Ct);
        await rotating.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM accounts WHERE id = {account.Id} FOR UPDATE", Ct);
        var rotationIat = await rotating.Database.NowAsync(Ct); // the iat of the access token this rotation would issue: its transaction's now()
        await rotating.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE refresh_tokens SET status = 'rotated', used_at = now() WHERE token_hash = {hash}", Ct);
        await rotating.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO refresh_tokens (id, family_id, account_id, token_hash, status, auth_time, expires_at, created_at)
            SELECT {Guid.NewGuid()}, family_id, account_id, {Opaque.Sha256(Opaque.NewToken())}, 'active', auth_time, expires_at, created_at
            FROM refresh_tokens WHERE token_hash = {hash}
            """, Ct);

        var signOut = t.SendAsync(HttpMethod.Post, "/api/me/sign-out-everywhere", caller: TestCaller.Customer(account.Id, t.Clock.GetUtcNow()));
        await Poll.UntilAsync(async () =>
        {
            await using var db = t.NewDb(); // a fresh transaction: pg_stat_activity is a per-transaction snapshot
            return (await db.Database.SqlQuery<string>(
                $"SELECT wait_event_type AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").ToListAsync(Ct)).Count > 0;
        },
        10, "sign-out-everywhere waiting on the rotation");
        var committingAt = (await rotating.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").ToListAsync(Ct)).Single();
        await tx.CommitAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, (await signOut).StatusCode);
        var rows = await RowsAsync(t, account.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(RefreshStatuses.Revoked, r.Status));
        var validAfter = (await t.AccountAsync(account.Id)).TokensValidAfter!.Value;
        Assert.True(rotationIat < validAfter, "the rotation's access token must count as revoked at the gateway (iat < tokens_valid_after)");
        Assert.True(committingAt < validAfter, "the stamp is taken after the lock wait, not at the start of the revoking transaction");
        Assert.Equal(validAfter.ToUnixTimeMilliseconds(), Assert.Single(await Outbox.EnvelopesAsync(t, "identity.tokens-revoked.v1")).GetPayload<TokensRevokedV1>().IssuedBefore);
    }

    [Fact]
    public async Task SignOut_revokes_only_that_family_and_clears_cookie()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        var a = await Seed.SessionAsync(t, account);
        var b = await Seed.SessionAsync(t, account);

        var response = await SignOutAsync(t, account, (await RowAsync(t, a.RefreshToken)).FamilyId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RefreshStatuses.Revoked, (await RowAsync(t, a.RefreshToken)).Status);
        Assert.Equal(RefreshStatuses.Active, (await RowAsync(t, b.RefreshToken)).Status);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("bloom_rt=")).ToLowerInvariant();
        Assert.StartsWith("bloom_rt=;", setCookie);
        Assert.Contains("expires=thu, 01 jan 1970", setCookie);
        Assert.Contains("path=/auth/refresh", setCookie);
        Assert.Empty(await Outbox.EnvelopesAsync(t, "identity.tokens-revoked.v1"));
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "sign_out")));
    }

    [Fact]
    public async Task SignOut_with_foreign_or_unknown_sid_is_NOT_FOUND()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        var own = await Seed.SessionAsync(t, account);
        var foreign = await Seed.SessionAsync(t, await Seed.CustomerAsync(t));

        await (await SignOutAsync(t, account, (await RowAsync(t, foreign.RefreshToken)).FamilyId)).ExpectErrorAsync(404, "NOT_FOUND");
        await (await SignOutAsync(t, account, Guid.NewGuid())).ExpectErrorAsync(404, "NOT_FOUND");
        await (await SignOutAsync(t, account, "not-a-guid")).ExpectErrorAsync(404, "NOT_FOUND");
        Assert.Equal(RefreshStatuses.Active, (await RowAsync(t, foreign.RefreshToken)).Status);

        var familyId = (await RowAsync(t, own.RefreshToken)).FamilyId;
        Assert.Equal(HttpStatusCode.OK, (await SignOutAsync(t, account, familyId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignOutAsync(t, account, familyId)).StatusCode); // already revoked
    }

    [Fact]
    public async Task SignOutEverywhere_revokes_all()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        await Seed.SessionAsync(t, account);
        await Seed.SessionAsync(t, account);

        var response = await t.SendAsync(HttpMethod.Post, "/api/me/sign-out-everywhere", caller: TestCaller.Customer(account.Id, t.Clock.GetUtcNow()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(await RowsAsync(t, account.Id), r => Assert.Equal(RefreshStatuses.Revoked, r.Status));
        await using var db = t.NewDb();
        Assert.NotNull((await db.Accounts.SingleAsync(x => x.Id == account.Id, Ct)).TokensValidAfter);
        Assert.Single(await Outbox.EnvelopesAsync(t, "identity.tokens-revoked.v1"));
        Assert.Equal(1, metrics.Sum("revocations_total", ("trigger", "sign_out_everywhere")));
    }

    private static Task<HttpResponseMessage> SignOutAsync(TestIdentity t, Account account, object sid) =>
        t.SendAsync(HttpMethod.Post, "/api/me/sign-out", new { sid }, TestCaller.Customer(account.Id, t.Clock.GetUtcNow()));

    [Fact]
    public async Task ID2_concurrent_revoke_alls_increment_version_twice()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        var account = await Seed.CustomerAsync(t);
        var caller = TestCaller.Customer(account.Id, t.Clock.GetUtcNow());
        var before = (await t.AccountAsync(account.Id)).Version;

        // Both requests read the account, then queue on its row; the second must build on the first's version, not on what it read.
        await using var held = await RowLock.HoldAsync(t, $"SELECT 1 FROM accounts WHERE id = {account.Id} FOR UPDATE");
        var revokes = new[] { SignOutEverywhereAsync(t, caller), SignOutEverywhereAsync(t, caller) };
        await RowLock.WaitForWaitersAsync(t, 2, "both revoke-alls waiting on the account row");
        await held.ReleaseAsync();

        foreach (var response in await Task.WhenAll(revokes)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before + 2, (await t.AccountAsync(account.Id)).Version);
    }

    [Fact]
    public async Task TryRotate_vs_SignOut_overlap_leaves_no_active_token()
    {
        await using var t = await TestIdentity.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = await Seed.CustomerAsync(t);
        var session = await Seed.SessionAsync(t, account);
        var familyId = (await RowAsync(t, session.RefreshToken)).FamilyId;

        // Both take the account row first (R41). Whichever runs first, the sign-out ends with the whole family revoked: either it
        // revoked the token before the refresh saw it, or it revoked the new token the refresh inserted.
        await using var held = await RowLock.HoldAsync(t, $"SELECT 1 FROM accounts WHERE id = {account.Id} FOR UPDATE");
        var refresh = RefreshAsync(t, session.RefreshToken);
        var signOut = SignOutAsync(t, account, familyId);
        await RowLock.WaitForWaitersAsync(t, 2, "the refresh and the sign-out waiting on the account row");
        await held.ReleaseAsync();

        Assert.Equal(HttpStatusCode.OK, (await signOut).StatusCode);
        Assert.Contains((await refresh).StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized });
        Assert.All(await RowsAsync(t, account.Id), r => Assert.NotEqual(RefreshStatuses.Active, r.Status));
        Assert.Equal(0, metrics.Sum("refresh_total", ("result", "reuse"))); // a revoked token is not reuse (D8)
        Assert.Empty(await Outbox.EnvelopesAsync(t, "identity.tokens-revoked.v1"));
    }

    private static Task<HttpResponseMessage> SignOutEverywhereAsync(TestIdentity t, TestCaller caller) =>
        t.SendAsync(HttpMethod.Post, "/api/me/sign-out-everywhere", caller: caller);
}
