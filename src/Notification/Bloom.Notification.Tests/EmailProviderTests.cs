using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using Bloom.Notification.Email;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Notification.Tests;

public class EmailProviderTests(Containers containers)
{
    private static readonly byte[] Key = Convert.FromBase64String(TestNotification.CodeKey);

    private static string Address() => $"{Guid.NewGuid():N}@customer.test";

    private static EmailMessage Message(string to) => new(to, "Your order", "<p>Hello <b>Al</b></p>", "Hello Al");

    private static IEmailProvider Provider(TestNotification n) => n.Services.GetRequiredService<IEmailProvider>();

    [Fact]
    public async Task Resend_sends_the_documented_request()
    {
        await using var n = await TestNotification.StartAsync(containers);
        var to = Address();
        var id = Guid.NewGuid();

        var messageId = await Provider(n).SendAsync(Message(to), $"bloom:email:{id:D}", TestContext.Current.CancellationToken);

        Assert.Equal("re_1", messageId);
        var request = Assert.Single(n.Resend.Requests);
        Assert.Equal("emails", request.Path);
        Assert.Equal("Bearer re_test", request.Authorization);
        Assert.Equal($"bloom:email:{id:D}", request.IdempotencyKey);
        Assert.Equal("Bloom <no-reply@bloom.test>", request.Body.GetProperty("from").GetString());
        Assert.Equal(to, Assert.Single(request.Body.GetProperty("to").EnumerateArray()).GetString());
        Assert.Equal("Your order", request.Body.GetProperty("subject").GetString());
        Assert.Equal("<p>Hello <b>Al</b></p>", request.Body.GetProperty("html").GetString());
        Assert.Equal("Hello Al", request.Body.GetProperty("text").GetString());
        Assert.Empty(n.Resend.Unexpected);
    }

    [Theory, InlineData(500), InlineData(503), InlineData(429)]
    public async Task Resend_server_errors_and_429_are_transient(int status)
    {
        await using var n = await TestNotification.StartAsync(containers);
        n.Resend.Fail(status);

        var e = await Assert.ThrowsAsync<EmailSendException>(() => Provider(n).SendAsync(Message(Address()), "bloom:email:k", TestContext.Current.CancellationToken));

        Assert.True(e.Transient);
        Assert.Equal($"http_{status}", e.Reason);
        Assert.Empty(n.Resend.Delivered);
    }

    [Fact]
    public async Task D39_429_carries_retry_after_seconds_or_date()
    {
        await using var n = await TestNotification.StartAsync(containers);
        n.Resend.Fail(429, retryAfter: "120");
        n.Resend.Fail(429, retryAfter: n.Clock.GetUtcNow().AddSeconds(90).ToString("R"));
        n.Resend.Fail(429, retryAfter: "soon");
        n.Resend.Fail(429);
        n.Resend.Fail(503, retryAfter: "120");
        var provider = Provider(n);

        async Task<EmailSendException> Fail() =>
            await Assert.ThrowsAsync<EmailSendException>(() => provider.SendAsync(Message(Address()), "bloom:email:k", TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(120), (await Fail()).RetryAfter);
        Assert.InRange((await Fail()).RetryAfter!.Value.TotalSeconds, 88, 90); // an HTTP-date, counted from the app clock; the header has whole seconds
        Assert.Equal(TimeSpan.Zero, (await Fail()).RetryAfter); // unparseable: as if absent
        Assert.Equal(TimeSpan.Zero, (await Fail()).RetryAfter);
        Assert.Null((await Fail()).RetryAfter); // only a 429 is throttled
    }

    [Fact]
    public async Task N5_smtp_timeout_path()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0); // accepts the connection and never sends the 220 greeting
        silent.Start();
        await using var n = await TestNotification.StartAsync(containers, o =>
        {
            o.Config["Notification:Email:Provider"] = "smtp";
            o.Config["Notification:Email:Smtp:Host"] = "127.0.0.1";
            o.Config["Notification:Email:Smtp:Port"] = ((IPEndPoint)silent.LocalEndpoint).Port.ToString();
        });

        var e = await Assert.ThrowsAsync<EmailSendException>(() => Provider(n).SendAsync(Message(Address()), "bloom:email:k", TestContext.Current.CancellationToken));

        Assert.True(e.Transient);
        Assert.Equal("timeout", e.Reason);
    }

    [Fact]
    public async Task Resend_4xx_is_permanent_with_its_name()
    {
        await using var n = await TestNotification.StartAsync(containers);
        n.Resend.Fail(422, "validation_error");
        n.Resend.Fail(400);
        var provider = Provider(n);

        var named = await Assert.ThrowsAsync<EmailSendException>(() => provider.SendAsync(Message(Address()), "bloom:email:a", TestContext.Current.CancellationToken));
        var unnamed = await Assert.ThrowsAsync<EmailSendException>(() => provider.SendAsync(Message(Address()), "bloom:email:b", TestContext.Current.CancellationToken));

        Assert.False(named.Transient);
        Assert.Equal("validation_error", named.Reason);
        Assert.False(unnamed.Transient);
        Assert.Equal("http_400", unnamed.Reason);
    }

    [Fact]
    public async Task Resend_times_out_after_10_seconds()
    {
        await using var n = await TestNotification.StartAsync(containers);
        n.Resend.HangNext();
        var clock = Stopwatch.StartNew(); // real time: HttpClient's timeout does not use the app's TimeProvider

        var e = await Assert.ThrowsAsync<EmailSendException>(() => Provider(n).SendAsync(Message(Address()), "bloom:email:k", TestContext.Current.CancellationToken));

        Assert.True(e.Transient);
        Assert.Equal("timeout", e.Reason);
        Assert.InRange(clock.Elapsed.TotalSeconds, 9.5, 13);
    }

    [Fact]
    public async Task Resend_caller_cancellation_is_not_a_timeout()
    {
        await using var n = await TestNotification.StartAsync(containers);
        n.Resend.HangNext();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(n).SendAsync(Message(Address()), "bloom:email:k", cts.Token));
    }

    [Fact]
    public async Task Resend_network_error_is_transient()
    {
        await using var n = await TestNotification.StartAsync(containers);
        n.Resend.Before = () => throw new HttpRequestException("connection refused");

        var e = await Assert.ThrowsAsync<EmailSendException>(() => Provider(n).SendAsync(Message(Address()), "bloom:email:k", TestContext.Current.CancellationToken));

        Assert.True(e.Transient);
        Assert.Equal("network", e.Reason);
    }

    [Fact]
    public async Task Smtp_sends_through_mailpit()
    {
        await using var n = await TestNotification.StartAsync(containers, o => o.Config["Notification:Email:Provider"] = "smtp");
        var to = Address();

        var messageId = await Provider(n).SendAsync(Message(to), "bloom:email:k", TestContext.Current.CancellationToken);

        Assert.Null(messageId);
        MailpitMessage? mail = null;
        await Poll.UntilAsync(async () => (mail = await Mailpit.FindAsync(containers, to)) is not null, 10, "the message in Mailpit");
        Assert.Equal("Your order", mail!.Subject);
        Assert.Contains("Hello Al", mail.Text);
        Assert.Contains("<p>Hello <b>Al</b></p>", mail.Html);
        Assert.Empty(n.Resend.Requests);
    }

    [Fact]
    public async Task Smtp_malformed_address_is_permanent()
    {
        await using var n = await TestNotification.StartAsync(containers, o => o.Config["Notification:Email:Provider"] = "smtp");

        var e = await Assert.ThrowsAsync<EmailSendException>(() => Provider(n).SendAsync(Message("not an address"), "bloom:email:k", TestContext.Current.CancellationToken));

        Assert.False(e.Transient);
        Assert.Equal("invalid_address", e.Reason);
        Assert.DoesNotContain("not an address", e.Message);
    }

    [Theory, InlineData(550, true), InlineData(553, true), InlineData(554, false), InlineData(421, false), InlineData(451, false)]
    public void Smtp_mailbox_errors_are_permanent(int code, bool permanent) =>
        Assert.Equal(permanent, SmtpEmailProvider.IsPermanent((SmtpStatusCode)code));

    [Fact]
    public void Code_decrypts_identity_format()
    {
        var sealedCode = TestNotification.EncryptCode("123456");

        Assert.Equal("123456", CodeCipher.Decrypt(Key, sealedCode));
        Assert.ThrowsAny<CryptographicException>(() => CodeCipher.Decrypt(Enumerable.Repeat((byte)9, 32).ToArray(), sealedCode));
        var raw = System.Buffers.Text.Base64Url.DecodeFromChars(sealedCode);
        raw[14] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => CodeCipher.Decrypt(Key, System.Buffers.Text.Base64Url.EncodeToString(raw)));
        Assert.ThrowsAny<CryptographicException>(() => CodeCipher.Decrypt(Key, System.Buffers.Text.Base64Url.EncodeToString(new byte[20])));
        Assert.Throws<FormatException>(() => CodeCipher.Decrypt(Key, "%%"));
    }
}
