using Bloom.Notification.Context;
using Bloom.Notification.Errors;
using Bloom.Shared.Errors;
using Google.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Bloom.Notification.Tests;

public class ShellTests(Containers containers)
{
    private static readonly Dictionary<string, string?> Valid = TestNotification.DefaultConfig("Host=localhost;Database=unused", "amqp://localhost:5672", "localhost:6379", "localhost", "1025");

    private static WebApplication Build(string mode, Dictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>(Valid) { ["Notification:Mode"] = mode };
        foreach (var (key, value) in overrides ?? []) config[key] = value;
        return NotificationApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
    }

    /// <summary>The Smtp keys only count with provider smtp, so those tests switch to it.</summary>
    private static Dictionary<string, string?> With(string key, string value) =>
        key.Contains(":Smtp:") ? new() { ["Notification:Email:Provider"] = "smtp", [key] = value } : new() { [key] = value };

    [Theory, InlineData("api"), InlineData("worker")]
    public async Task Each_mode_builds_with_valid_config(string mode)
    {
        await using var app = Build(mode);

        Assert.NotNull(app);
    }

    [Fact]
    public void Unknown_mode_fails_at_build()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("edge"));
        Assert.Contains("Notification:Mode", e.Message);
    }

    // Every key of plan N4 except Mode (which has its own test), in both modes.
    public static TheoryData<string, string> RequiredKeys() =>
        new(from mode in new[] { "api", "worker" } from key in Valid.Keys select (mode, key));

    [Theory, MemberData(nameof(RequiredKeys))]
    public void Missing_config_fails_at_build_naming_the_key(string mode, string key)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build(mode, With(key, "")));
        Assert.Contains(key, e.Message);
    }

    public static TheoryData<string, string> InvalidValues() => new()
    {
        { "Notification:StorefrontBaseUrl", "shop" },
        { "Notification:Email:Resend:BaseUrl", "resend" },
        { "Notification:CodeEncryptionKey", Convert.ToBase64String(new byte[31]) },
        { "Notification:CodeEncryptionKey", "not base64!" },
        { "Notification:Email:Smtp:Port", "0" },
        { "Notification:Email:Smtp:Port", "70000" },
        { "Notification:Email:Provider", "ses" },
        { "Notification:AllowedOrigins:0", "shop.test" },
    };

    [Theory, MemberData(nameof(InvalidValues))]
    public void Invalid_config_fails_at_build_naming_the_key(string key, string value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", With(key, value)));
        Assert.Contains(key, e.Message);
    }

    [Fact]
    public void D40_malformed_from_fails_startup_naming_key_not_value()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("worker", With("Notification:Email:From", "not-an-address-ZZZ")));

        Assert.Contains("Notification:Email:From", e.Message);
        Assert.DoesNotContain("ZZZ", e.Message);
    }

    [Theory]
    [InlineData("Notification:StorefrontBaseUrl", "javascript:alert(1)")]
    [InlineData("Notification:StorefrontBaseUrl", "ftp://shop.test")]
    [InlineData("Notification:Email:Resend:BaseUrl", "file:///etc/passwd")]
    [InlineData("Notification:AllowedOrigins:0", "ftp://shop.test")]
    public void N3_non_http_url_rejected(string key, string value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", With(key, value)));

        Assert.Contains(key, e.Message);
    }

    [Fact]
    public async Task N4_redis_connects_at_startup()
    {
        // Build needs no Redis, but starting the worker connects at once: with nothing listening it fails now, not on the first push.
        var unreachable = "127.0.0.1:1,connectTimeout=1000"; // port 1: nothing listens
        await Assert.ThrowsAsync<StackExchange.Redis.RedisConnectionException>(() =>
            TestNotification.StartAsync(containers, o => { o.Mode = "worker"; o.Config["Notification:Redis"] = unreachable; }));
    }

    [Fact]
    public async Task Smtp_needs_no_resend_keys()
    {
        await using var app = Build("api", new()
        {
            ["Notification:Email:Provider"] = "smtp",
            ["Notification:Email:Resend:ApiKey"] = "",
            ["Notification:Email:Resend:BaseUrl"] = "",
        });

        Assert.NotNull(app);
    }

    public static TheoryData<string, StatusCode> ReasonTable() => new()
    {
        { "VALIDATION_FAILED", StatusCode.InvalidArgument }, { "ROLE_NOT_ALLOWED", StatusCode.PermissionDenied },
    };

    [Theory, MemberData(nameof(ReasonTable))]
    public void Each_reason_has_its_status(string reason, StatusCode code)
    {
        var e = reason == NotificationErrors.ValidationFailed
            ? NotificationErrors.Validation("page_token", "m")
            : Assert.Throws<RpcException>(() => new Caller(Guid.NewGuid(), "staff").RequireCustomer());

        Assert.Equal(code, e.StatusCode);
        var info = ErrorInfoOf(e);
        Assert.Equal(reason, info.Reason);
        Assert.Equal("bloom", info.Domain);
    }

    [Fact]
    public void Role_checks()
    {
        var customer = new Caller(Guid.NewGuid(), "customer");

        Assert.Same(customer, customer.RequireCustomer());
        Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => new Caller(Guid.NewGuid(), "staff").RequireCustomer()).StatusCode);
    }

    [Fact]
    public async Task Api_host_starts_and_hosts_the_dlq_admin()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var admin = t.StaffClient("admin");

        var response = await admin.GetAsync("/api/admin/dlq/notification/queues", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private static ErrorInfo ErrorInfoOf(RpcException e) =>
        e.GetRpcStatus()!.Details.Single().Unpack<ErrorInfo>();
}
