using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bloom.Identity.Contracts;
using Bloom.Identity.Crypto;
using Bloom.Identity.Data;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bloom.Identity.Tests;

public class SecretHygieneTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ID_T01d_no_secret_reaches_logs_or_outbox()
    {
        await using var t = await TestIdentity.StartAsync(containers, o => o.Config["Logging:LogLevel:Default"] = "Trace");
        var secrets = new List<string>();
        var codes = new List<string>(); // six digits: matched as whole tokens only
        var email = $"{Guid.NewGuid():N}@example.com";
        const string signUpPassword = "Sign-up pw 7734 horse", resetPassword = "Reset pw 9981 battery";

        // Customer: sign-up, sign-in, refresh, password reset.
        Assert.Equal(HttpStatusCode.OK, (await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/start", new { email })).StatusCode);
        var signUpCode = await Outbox.LatestCodeAsync(t, email, "signup");
        var verify = await (await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/verify", new { email, code = signUpCode })).OkJsonAsync();
        var registrationToken = verify.GetProperty("registrationToken").GetString()!;
        var complete = await t.SendAsync(HttpMethod.Post, "/api/auth/sign-up/complete", new { registrationToken, password = signUpPassword, name = "Ann Lee" });
        var signUpAccess = (await complete.OkJsonAsync()).GetProperty("accessToken").GetString()!;
        var signUpRefresh = complete.RefreshCookie()!;

        var signIn = await t.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email, password = signUpPassword });
        var signInAccess = (await signIn.OkJsonAsync()).GetProperty("accessToken").GetString()!;
        var signInRefresh = signIn.RefreshCookie()!;

        var refresh = await t.SendAsync(HttpMethod.Post, "/auth/refresh", cookie: $"bloom_rt={signInRefresh}");
        var refreshAccess = (await refresh.OkJsonAsync()).GetProperty("accessToken").GetString()!;
        var refreshRefresh = refresh.RefreshCookie()!;

        Assert.Equal(HttpStatusCode.OK, (await t.SendAsync(HttpMethod.Post, "/api/auth/password-reset/request", new { email })).StatusCode);
        var resetCode = await Outbox.LatestCodeAsync(t, email, "reset_password");
        Assert.Equal(HttpStatusCode.OK,
            (await t.SendAsync(HttpMethod.Post, "/api/auth/password-reset/confirm", new { email, code = resetCode, newPassword = resetPassword })).StatusCode);

        // Staff: invite, accept with TOTP enrolment, sign in with the next step's code.
        var (admin, _) = await Seed.StaffAsync(t, StaffRoles.Admin);
        var staffEmail = $"{Guid.NewGuid():N}@bloom.example";
        var invite = await t.SendAsync(HttpMethod.Post, "/api/admin/staff/invitations", new { email = staffEmail, role = StaffRoles.Support },
            TestCaller.Staff(admin.Id, StaffRoles.Admin, t.Clock.GetUtcNow()));
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        var inviteToken = (await Outbox.EnvelopesAsync(t, IdentityMessages.StaffInvited)).Single().GetPayload<StaffInvitedV1>().InviteUrl.Split("?token=")[1];
        var (totpText, totp) = TestTotp.NewSecret();
        long Step() => t.Clock.GetUtcNow().ToUnixTimeSeconds() / 30;
        var acceptCode = Totp.Compute(totp, Step());
        var accept = await t.SendAsync(HttpMethod.Post, "/api/auth/staff/invitations/accept",
            new { inviteToken, name = "New Staff", password = Seed.Password, totpSecret = totpText, totpCode = acceptCode });
        var acceptAccess = (await accept.OkJsonAsync()).GetProperty("accessToken").GetString()!;
        var acceptRefresh = accept.RefreshCookie()!;

        var staffSignIn = await (await t.SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email = staffEmail, password = Seed.Password })).OkJsonAsync();
        var challenge = staffSignIn.GetProperty("mfaChallengeId").GetString()!;
        var mfaCode = Totp.Compute(totp, Step() + 1);
        var mfa = await t.SendAsync(HttpMethod.Post, "/api/auth/staff/mfa", new { mfaChallengeId = challenge, code = mfaCode });
        var mfaAccess = (await mfa.OkJsonAsync()).GetProperty("accessToken").GetString()!;
        var mfaRefresh = mfa.RefreshCookie()!;

        secrets.AddRange([signUpPassword, resetPassword, Seed.Password, registrationToken, inviteToken, totpText,
            Convert.ToBase64String(totp), Convert.ToHexString(totp), Convert.ToHexStringLower(totp),
            signUpAccess, signUpRefresh, signInAccess, signInRefresh, refreshAccess, refreshRefresh, acceptAccess, acceptRefresh, mfaAccess, mfaRefresh]);
        codes.AddRange([signUpCode, resetCode, acceptCode, mfaCode]);
        Assert.All(secrets, s => Assert.False(string.IsNullOrEmpty(s)));

        // Logs: message + exception text, state values and scope values of every entry.
        Assert.Contains(t.Logs.Entries, e => e.Level == LogLevel.Trace);
        var logText = t.Logs.Entries.Select(e => string.Join('\n', [e.Message, .. e.State.Select(kv => $"{kv.Key}={kv.Value}")])).ToList();
        AssertClean(logText, secrets, codes, "log");

        // Outbox: every payload, except the invite token inside staff-invited's inviteUrl.
        await using var db = t.NewDb();
        var outbox = await db.Set<OutboxMessage>().Select(m => new { m.MessageType, m.Payload }).ToListAsync(Ct);
        Assert.NotEmpty(outbox);
        AssertClean(outbox.Where(m => m.MessageType != IdentityMessages.StaffInvited).Select(m => m.Payload).ToList(), secrets, codes, "outbox");
        var invitedPayload = outbox.Single(m => m.MessageType == IdentityMessages.StaffInvited).Payload;
        AssertClean([invitedPayload.Replace(inviteToken, "<invite-token>")], secrets, codes, "staff-invited");
        Assert.Contains(inviteToken, invitedPayload);
    }

    private static void AssertClean(IReadOnlyList<string> texts, IEnumerable<string> secrets, IEnumerable<string> codes, string where)
    {
        foreach (var secret in secrets)
            Assert.False(texts.Any(x => x.Contains(secret, StringComparison.Ordinal)), $"A secret ({secret.Length} chars, starts '{secret[..2]}') reached the {where}.");
        foreach (var code in codes)
            Assert.False(texts.Any(x => Regex.IsMatch(x, $"(?<![0-9A-Za-z]){code}(?![0-9A-Za-z])")), $"A code ({code}) reached the {where}.");
    }
}
