using Bloom.Launch.Contracts;
using Bloom.Launch.Context;
using Bloom.Launch.Errors;
using Bloom.Shared.Errors;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Launch.Tests;

public class ShellTests(Containers containers)
{
    private static readonly Dictionary<string, string?> Valid = TestLaunch.DefaultConfig("Host=localhost;Database=unused", "amqp://localhost:5672", "localhost:6379", "http://localhost:8080");

    private static WebApplication Build(string mode, Dictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>(Valid) { ["Launch:Mode"] = mode };
        foreach (var (key, value) in overrides ?? []) config[key] = value;
        return LaunchApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
    }

    [Theory, InlineData("api"), InlineData("worker")]
    public async Task Each_mode_builds_with_valid_config(string mode)
    {
        await using var app = Build(mode);

        Assert.Equal(mode == "api", app.Services.GetRequiredService<LaunchOptions>().IsApi);
    }

    [Fact]
    public void Unknown_mode_fails_at_build()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("edge"));
        Assert.Contains("Launch:Mode", e.Message);
    }

    // Every key of plan R4 except Mode (which has its own test) and AdmissionCeiling (which has a default).
    public static TheoryData<string> RequiredKeys() => new(Valid.Keys.Where(k => k != "Launch:AdmissionCeiling"));

    [Theory, MemberData(nameof(RequiredKeys))]
    public void Missing_config_fails_at_build_naming_the_key(string key)
    {
        foreach (var mode in new[] { "api", "worker" })
        {
            var e = Assert.Throws<InvalidOperationException>(() => Build(mode, new() { [key] = "" }));
            Assert.Contains(key, e.Message);
        }
    }

    [Theory, InlineData("localhost:6379,password=SECRET,bogus=1"), InlineData("localhost:notaport,password=SECRET")]
    public void Malformed_redis_fails_at_build_without_the_value(string redis)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", new() { ["Launch:Redis"] = redis }));

        Assert.Contains("Launch:Redis", e.Message);
        Assert.DoesNotContain("SECRET", e.Message);
    }

    [Fact]
    public void Relative_catalog_url_fails_at_build()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", new() { ["Launch:Catalog"] = "catalog" }));
        Assert.Contains("Launch:Catalog", e.Message);
    }

    [Fact]
    public void Bad_pass_keys_fail_at_build_without_the_key_value()
    {
        var short31 = Convert.ToBase64String(Enumerable.Repeat((byte)7, 31).ToArray());
        const string notBase64 = "SECRET-not-base64!";
        var cases = new (string Path, Dictionary<string, string?> Overrides)[]
        {
            ("Launch:PassKeys:0:Key", new() { ["Launch:PassKeys:0:Key"] = short31 }),
            ("Launch:PassKeys:0:Key", new() { ["Launch:PassKeys:0:Key"] = notBase64 }),
            ("Launch:PassKeys:1:Kid", new() { ["Launch:PassKeys:1:Kid"] = "k1" }),
            ("Launch:PassKeys:0:Kid", new() { ["Launch:PassKeys:0:Kid"] = "" }),
            ("Launch:CurrentPassKid", new() { ["Launch:CurrentPassKid"] = "k9" }),
        };
        foreach (var (path, overrides) in cases)
        {
            var e = Assert.Throws<InvalidOperationException>(() => Build("api", overrides));
            Assert.Contains(path, e.Message);
            foreach (var key in new[] { short31, notBase64, Valid["Launch:PassKeys:0:Key"]!, Valid["Launch:PassKeys:1:Key"]! })
                Assert.DoesNotContain(key, e.Message);
        }
    }

    [Fact]
    public void Empty_pass_key_list_fails()
    {
        var e = Assert.Throws<InvalidOperationException>(() => new LaunchOptions
        {
            Mode = "api", Database = "d", RabbitMq = "r", Redis = "x", Catalog = "http://c", CurrentPassKid = "k1",
        }.Validate());
        Assert.Contains("Launch:PassKeys", e.Message);
    }

    [Fact]
    public void Admission_ceiling_defaults_to_200()
    {
        Assert.Equal(200, new LaunchOptions().AdmissionCeiling);

        var e = Assert.Throws<InvalidOperationException>(() => Build("api", new() { ["Launch:AdmissionCeiling"] = "0" }));
        Assert.Contains("Launch:AdmissionCeiling", e.Message);
    }

    [Fact]
    public async Task Valid_keys_are_decoded_by_kid()
    {
        await using var app = Build("api");

        var keys = app.Services.GetRequiredService<LaunchOptions>().DecodedKeys;
        Assert.Equal(["k0", "k1"], keys.Keys.Order());
        Assert.Equal(Convert.FromBase64String(Valid["Launch:PassKeys:0:Key"]!), keys["k1"]);
    }

    public static TheoryData<string, StatusCode> ReasonTable() => new()
    {
        { "WAITING_ROOM_NOT_OPEN", StatusCode.FailedPrecondition }, { "WAITING_ROOM_CLOSED", StatusCode.FailedPrecondition },
        { "PASS_INVALID", StatusCode.FailedPrecondition }, { "PASS_EXPIRED", StatusCode.FailedPrecondition },
        { "PASS_ALREADY_USED", StatusCode.FailedPrecondition }, { "LAUNCH_NOT_OPEN", StatusCode.FailedPrecondition },
        { "LIMIT_EXCEEDED", StatusCode.FailedPrecondition }, { "SOLD_OUT", StatusCode.FailedPrecondition },
        { "ALLOCATION_DECREASE_NOT_ALLOWED", StatusCode.FailedPrecondition }, { "PURCHASE_LIMIT_LOCKED", StatusCode.FailedPrecondition },
        { "INVALID_STAGE", StatusCode.FailedPrecondition },
        { "SKU_NOT_IN_LAUNCH", StatusCode.InvalidArgument }, { "HOLD_CONFLICT", StatusCode.InvalidArgument },
        { "VALIDATION_FAILED", StatusCode.InvalidArgument },
        { "NOT_FOUND", StatusCode.NotFound }, { "NOT_IN_QUEUE", StatusCode.NotFound },
        { "ROLE_NOT_ALLOWED", StatusCode.PermissionDenied },
        { "CONCURRENCY_CONFLICT", StatusCode.Aborted },
    };

    [Theory, MemberData(nameof(ReasonTable))]
    public void Each_reason_has_its_status(string reason, StatusCode code)
    {
        var e = LaunchErrors.Error(reason, "m");

        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Rpc.Info(e).Reason);
        Assert.Equal("bloom", Rpc.Info(e).Domain);
    }

    [Fact]
    public void Helpers_carry_metadata_and_status()
    {
        var limit = LaunchErrors.Error(LaunchErrors.LimitExceeded, "m", ("productId", "p"), ("remaining", "2"));
        Assert.Equal("p", Rpc.Info(limit).Metadata["productId"]);
        Assert.Equal("2", Rpc.Info(limit).Metadata["remaining"]);

        var v = LaunchErrors.Validation("order_id", "m");
        Assert.Equal(StatusCode.InvalidArgument, v.StatusCode);
        Assert.Equal("VALIDATION_FAILED", Rpc.Info(v).Reason);
        Assert.Equal("order_id", Rpc.Info(v).Metadata["field"]);

        Assert.Equal(StatusCode.NotFound, LaunchErrors.NotFound("The launch").StatusCode);

        var unavailable = LaunchErrors.Unavailable("Redis is down.");
        Assert.Equal(StatusCode.Unavailable, unavailable.StatusCode);
        Assert.Null(unavailable.GetRpcStatus()); // no ErrorInfo: a plain status
    }

    [Fact]
    public void Role_checks()
    {
        var customer = new Caller(Guid.NewGuid(), "customer", null);
        var staff = new Caller(Guid.NewGuid(), "staff", "support");

        Assert.Same(customer, customer.RequireCustomer());
        Assert.Throws<RpcException>(() => staff.RequireCustomer());
        foreach (var role in new[] { "catalog_manager", "admin" })
            Assert.Equal(role, (staff with { Role = role }).RequireStaff(Caller.Staff).Role);
        foreach (var e in new[]
        {
            Assert.Throws<RpcException>(() => staff.RequireStaff(Caller.Staff)),
            Assert.Throws<RpcException>(() => (staff with { Role = "warehouse" }).RequireStaff(Caller.Staff)),
            Assert.Throws<RpcException>(() => customer.RequireStaff(Caller.Staff)),
            Assert.Throws<RpcException>(() => staff.RequireCustomer()),
        })
        {
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
            Assert.Equal(BloomErrors.RoleNotAllowed, Rpc.Info(e).Reason);
        }
    }

    [Fact]
    public async Task Api_host_starts_without_redis()
    {
        await using var t = await TestLaunch.StartAsync(containers, o => o.Config["Launch:Redis"] = "127.0.0.1:1");

        var info = await Rpc.FailsAsync(
            t.Internal.HoldAllocationAsync(new() { OrderId = "x" }, Api.Customer(Guid.NewGuid()), cancellationToken: TestContext.Current.CancellationToken), "VALIDATION_FAILED");
        Assert.Equal("order_id", info.Metadata["field"]);
    }

    public static TheoryData<string, string, string> BadHolds() => new()
    {
        { "not-a-uuid", "", "order_id" },
        { "", "", "lines" },
        { "", "bad:1", "lines.sku_id" },
        { "", "dup:1,dup:2", "lines.sku_id" },
        { "", "ok:0", "lines.quantity" },
    };

    [Theory, MemberData(nameof(BadHolds))]
    public async Task HoldAllocation_validates_the_request(string orderId, string lines, string field)
    {
        await using var t = await TestLaunch.StartAsync(containers);
        var sku = Guid.NewGuid().ToString("D");
        var request = new HoldAllocationRequest { OrderId = orderId == "" ? Guid.NewGuid().ToString("D") : orderId };
        foreach (var line in lines.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(':')))
            request.Lines.Add(new HoldLine { SkuId = line[0] switch { "ok" or "dup" => sku, var other => other }, Quantity = int.Parse(line[1]) });

        var info = await Rpc.FailsAsync(t.Internal.HoldAllocationAsync(request, Api.Customer(Guid.NewGuid()), cancellationToken: TestContext.Current.CancellationToken), "VALIDATION_FAILED");

        Assert.Equal(field, info.Metadata["field"]);
    }

    [Fact]
    public async Task HoldAllocation_is_for_customers()
    {
        await using var t = await TestLaunch.StartAsync(containers);

        await Rpc.FailsAsync(t.Internal.HoldAllocationAsync(new() { OrderId = "x" }, Api.Staff(), cancellationToken: TestContext.Current.CancellationToken), "ROLE_NOT_ALLOWED");
    }

    [Fact]
    public async Task Health_endpoints_answer_200_in_api_mode() // HLD-ADR-020
    {
        await using var t = await TestLaunch.StartAsync(containers);

        foreach (var path in new[] { "/healthz", "/readyz" })
        {
            using var response = await t.Http.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }
}
