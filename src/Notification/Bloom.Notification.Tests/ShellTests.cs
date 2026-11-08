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
            : Assert.Throws<RpcException>(() => new Caller(Guid.NewGuid(), "staff", "admin").RequireCustomer());

        Assert.Equal(code, e.StatusCode);
        var info = ErrorInfoOf(e);
        Assert.Equal(reason, info.Reason);
        Assert.Equal("bloom", info.Domain);
    }

    [Fact]
    public void Role_checks()
    {
        var customer = new Caller(Guid.NewGuid(), "customer", null);

        Assert.Same(customer, customer.RequireCustomer());
        foreach (var role in new[] { "support", "warehouse", "catalog_manager", "admin" })
            Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<RpcException>(() => new Caller(Guid.NewGuid(), "staff", role).RequireCustomer()).StatusCode);
    }

    [Fact]
    public async Task Api_host_starts_and_hosts_the_dlq_admin()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dlq/notification/queues");
        request.Headers.Add("x-bloom-user-type", "staff");
        request.Headers.Add("x-bloom-role", "Admin");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());

        var response = await t.Http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private static ErrorInfo ErrorInfoOf(RpcException e) =>
        Google.Rpc.Status.Parser.ParseFrom(e.Trailers.GetValueBytes("grpc-status-details-bin")).Details.Single().Unpack<ErrorInfo>();
}
