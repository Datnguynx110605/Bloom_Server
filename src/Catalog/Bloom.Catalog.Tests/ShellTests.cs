using Bloom.Catalog.Context;
using Bloom.Catalog.Errors;
using Bloom.Shared.Errors;
using Google.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Bloom.Catalog.Tests;

public class ShellTests(Containers containers)
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["Catalog:Database"] = "Host=localhost;Database=unused",
        ["Catalog:Redis"] = "localhost:6379",
        ["Catalog:RabbitMq"] = "amqp://localhost:5672",
        ["Catalog:Storage:ServiceUrl"] = "http://localhost:9000",
        ["Catalog:Storage:PublicBaseUrl"] = "http://localhost:9000/bloom-product-images",
        ["Catalog:Storage:AccessKey"] = "access",
        ["Catalog:Storage:SecretKey"] = "secret",
    };

    private static WebApplication Build(string mode, Dictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>(Valid) { ["Catalog:Mode"] = mode };
        foreach (var (key, value) in overrides ?? []) config[key] = value;
        return CatalogApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
    }

    [Fact]
    public void Unknown_mode_fails_at_build()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("edge"));
        Assert.Contains("Catalog:Mode", e.Message);
    }

    [Theory, InlineData("Catalog:Database"), InlineData("Catalog:Redis"), InlineData("Catalog:Storage:ServiceUrl"),
     InlineData("Catalog:Storage:PublicBaseUrl"), InlineData("Catalog:Storage:AccessKey")]
    public void Missing_config_fails_at_build_naming_the_key(string key)
    {
        foreach (var mode in new[] { "api", "worker" })
        {
            var e = Assert.Throws<InvalidOperationException>(() => Build(mode, new() { [key] = "" }));
            Assert.Contains(key, e.Message);
        }
    }

    public static TheoryData<string, StatusCode> ReasonTable() => new()
    {
        { "SERIALIZED_LOCKED", StatusCode.FailedPrecondition }, { "NOT_VERIFIED_BUYER", StatusCode.PermissionDenied },
        { "DUPLICATE_COMBINATION", StatusCode.AlreadyExists }, { "IMAGE_INVALID", StatusCode.InvalidArgument },
        { "CATEGORY_DEPTH", StatusCode.InvalidArgument }, { "NOT_FOUND", StatusCode.NotFound },
        { "INVALID_LIFECYCLE", StatusCode.FailedPrecondition }, { "VALIDATION_FAILED", StatusCode.InvalidArgument },
        { "PUBLISH_INCOMPLETE", StatusCode.FailedPrecondition }, { "SLUG_TAKEN", StatusCode.AlreadyExists },
        { "SKU_CODE_TAKEN", StatusCode.AlreadyExists },
    };

    private static ErrorInfo Info(RpcException e) => e.GetRpcStatus()!.Details.Single().Unpack<ErrorInfo>();

    [Theory, MemberData(nameof(ReasonTable))]
    public void Each_reason_has_its_status(string reason, StatusCode code)
    {
        var e = CatalogErrors.Error(reason, "m");

        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Info(e).Reason);
        Assert.Equal("bloom", Info(e).Domain);
    }

    [Fact]
    public void Validation_error_carries_the_field()
    {
        var e = CatalogErrors.Validation("slug", "bad");

        Assert.Equal(StatusCode.InvalidArgument, e.StatusCode);
        Assert.Equal("VALIDATION_FAILED", Info(e).Reason);
        Assert.Equal("bloom", Info(e).Domain);
        Assert.Equal("slug", Info(e).Metadata["field"]);
    }

    [Fact]
    public void Role_checks()
    {
        var manager = new Caller(Guid.NewGuid(), "staff", "catalog_manager");
        var support = manager with { Role = "support" };
        var customer = new Caller(Guid.NewGuid(), "customer", null);

        Assert.Same(manager, manager.RequireStaff(Caller.CatalogRoles));
        Assert.Same(support, support.RequireStaff(Caller.ModerationRoles));
        Assert.Same(customer, customer.RequireCustomer());
        foreach (var e in new[]
        {
            Assert.Throws<RpcException>(() => support.RequireStaff(Caller.CatalogRoles)),
            Assert.Throws<RpcException>(() => customer.RequireStaff(Caller.CatalogRoles)),
            Assert.Throws<RpcException>(() => manager.RequireCustomer()),
        })
        {
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
            Assert.Equal(BloomErrors.RoleNotAllowed, Info(e).Reason);
        }
    }

    [Fact]
    public async Task Api_host_starts_on_test_infrastructure()
    {
        await using var t = await TestCatalog.StartAsync(containers);

        var buckets = await containers.S3.ListBucketsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(buckets.Buckets, b => b.BucketName == t.Bucket);
        Assert.True(t.Redis.Database > 0);
        await using var db = t.NewDb();
        Assert.True(await db.Database.CanConnectAsync(TestContext.Current.CancellationToken));
    }
}
