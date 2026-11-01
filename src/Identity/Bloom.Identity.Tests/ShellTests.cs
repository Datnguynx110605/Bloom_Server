using Microsoft.AspNetCore.Builder;
using Bloom.Identity.Errors;
using Bloom.Identity.Context;
using Bloom.Shared.Errors;
using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Identity.Tests;

public class ShellTests(Containers containers)
{
    private static readonly TestSecrets Secrets = TestSecrets.Create();

    private static WebApplication Build(Dictionary<string, string?> overrides, bool apiSecrets = true)
    {
        var config = new Dictionary<string, string?>
        {
            ["Identity:Database"] = "Host=localhost;Database=unused",
            ["Identity:Redis"] = "localhost:6379",
            ["Identity:RabbitMq"] = "amqp://localhost:5672",
        };
        if (apiSecrets)
            foreach (var (key, value) in Secrets.ToConfig()) config[key] = value;
        foreach (var (key, value) in overrides) config[key] = value;
        return IdentityApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
    }

    [Fact]
    public void Unknown_mode_fails_at_build()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build(new() { ["Identity:Mode"] = "edge" }));
        Assert.Contains("Identity:Mode", e.Message);
    }

    [Theory, InlineData("Identity:CodePepper", "c2hvcnQ="), InlineData("Identity:ActiveKeyId", "nope"),
     InlineData("Identity:DataProtection:Certificate", null)]
    public void Invalid_config_fails_at_build_naming_the_key(string key, string? value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build(new() { ["Identity:Mode"] = "api", [key] = value }));
        Assert.Contains(key, e.Message);
    }

    [Fact]
    public async Task Worker_mode_needs_no_api_secrets()
    {
        await using var app = Build(new() { ["Identity:Mode"] = "worker" }, apiSecrets: false);
        Assert.NotNull(app);
    }

    [Theory, InlineData("api"), InlineData("worker")]
    public async Task Test_host_starts_in_both_modes(string mode)
    {
        await using var identity = await TestIdentity.StartAsync(containers, o => o.Mode = mode);
        await using var db = identity.NewDb();
        Assert.True(await db.Database.CanConnectAsync(TestContext.Current.CancellationToken));
    }

    public static TheoryData<string, StatusCode> ReasonTable() => new()
    {
        { "INVALID_CREDENTIALS", StatusCode.Unauthenticated }, { "REFRESH_INVALID", StatusCode.Unauthenticated },
        { "MFA_INVALID", StatusCode.Unauthenticated }, { "UNSETTLED_ORDERS", StatusCode.FailedPrecondition },
        { "EMAIL_ALREADY_REGISTERED", StatusCode.AlreadyExists }, { "ORDER_SERVICE_UNAVAILABLE", StatusCode.Unavailable },
        { "EMAIL_TAKEN", StatusCode.AlreadyExists }, { "LAST_ADMIN", StatusCode.FailedPrecondition },
        { "REGISTRATION_TOKEN_INVALID", StatusCode.FailedPrecondition }, { "INVITATION_INVALID", StatusCode.FailedPrecondition },
        { "PASSWORD_WEAK", StatusCode.InvalidArgument }, { "VALIDATION_FAILED", StatusCode.InvalidArgument },
        { "CODE_INVALID", StatusCode.FailedPrecondition }, { "NOT_FOUND", StatusCode.NotFound },
        { "CODE_RATE_LIMITED", StatusCode.ResourceExhausted }, { "ADDRESS_LIMIT_REACHED", StatusCode.FailedPrecondition },
        { "REAUTH_REQUIRED", StatusCode.FailedPrecondition }, { "PASSWORD_REQUIRED", StatusCode.FailedPrecondition },
        { "GOOGLE_NOT_ALLOWED", StatusCode.FailedPrecondition }, { "GOOGLE_ALREADY_LINKED", StatusCode.AlreadyExists },
    };

    private static ErrorInfo Info(RpcException e) => e.GetRpcStatus()!.Details.Single().Unpack<ErrorInfo>();

    [Theory, MemberData(nameof(ReasonTable))]
    public void Each_reason_has_its_status(string reason, StatusCode code)
    {
        var e = IdentityErrors.Error(reason, "m");

        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Info(e).Reason);
        Assert.Equal("bloom", Info(e).Domain);
    }

    [Fact]
    public void Validation_error_carries_the_field()
    {
        var e = IdentityErrors.Validation("email", "bad");

        Assert.Equal(StatusCode.InvalidArgument, e.StatusCode);
        Assert.Equal("VALIDATION_FAILED", Info(e).Reason);
        Assert.Equal("bloom", Info(e).Domain);
        Assert.Equal("email", Info(e).Metadata["field"]);
    }

    [Fact]
    public void Recent_auth_boundary_is_five_minutes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var caller = new TrustedCaller(Guid.NewGuid(), "customer", null, clock.GetUtcNow() - TimeSpan.FromSeconds(300));

        Assert.Same(caller, caller.RequireRecentAuth(clock));

        clock.Advance(TimeSpan.FromSeconds(1)); // now 301 s
        var e = Assert.Throws<RpcException>(() => caller.RequireRecentAuth(clock));
        Assert.Equal("REAUTH_REQUIRED", Info(e).Reason);
    }

    [Fact]
    public void Customer_only_and_admin_only_checks()
    {
        var customer = new TrustedCaller(Guid.NewGuid(), "customer", null, DateTimeOffset.UtcNow);
        var support = new TrustedCaller(Guid.NewGuid(), "staff", "support", DateTimeOffset.UtcNow);
        var admin = support with { Role = "admin" };

        Assert.Same(customer, customer.RequireCustomer());
        Assert.Same(admin, admin.RequireAdmin());
        foreach (var e in new[]
        {
            Assert.Throws<RpcException>(() => support.RequireCustomer()),
            Assert.Throws<RpcException>(() => support.RequireAdmin()),
            Assert.Throws<RpcException>(() => customer.RequireAdmin()),
        })
        {
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
            Assert.Equal(BloomErrors.RoleNotAllowed, Info(e).Reason);
        }
    }
}
