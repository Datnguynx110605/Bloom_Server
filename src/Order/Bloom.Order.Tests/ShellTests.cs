using System.Text.Json;
using Bloom.Order.Api;
using Bloom.Order.Contracts;
using Bloom.Order.Context;
using Bloom.Order.Errors;
using Bloom.Order.Observability;
using Bloom.Launch.Contracts;
using Bloom.Payment.Contracts;
using Bloom.Shared.Errors;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Order.Tests;

public class ShellTests(Containers containers)
{
    private static readonly Dictionary<string, string?> Valid = TestOrder.DefaultConfig("Host=localhost;Database=unused", "amqp://localhost:5672", "http://localhost:8080");

    private static WebApplication Build(string mode, Dictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>(Valid) { ["Order:Mode"] = mode };
        foreach (var (key, value) in overrides ?? []) config[key] = value;
        return OrderApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
    }

    [Theory, InlineData("api"), InlineData("worker")]
    public async Task Each_mode_builds_with_valid_config(string mode)
    {
        await using var app = Build(mode); // a gRPC service mapped in worker mode throws here: AddGrpc is api only

        Assert.NotNull(app);
    }

    [Fact]
    public void Unknown_mode_fails_at_build()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("edge"));
        Assert.Contains("Order:Mode", e.Message);
    }

    // Every key of plan R4 except Mode (which has its own test).
    public static TheoryData<string> RequiredKeys() => new(Valid.Keys);

    [Theory, MemberData(nameof(RequiredKeys))]
    public void Missing_config_fails_at_build_naming_the_key(string key)
    {
        foreach (var mode in new[] { "api", "worker" })
        {
            var e = Assert.Throws<InvalidOperationException>(() => Build(mode, new() { [key] = "" }));
            Assert.Contains(key, e.Message);
        }
    }

    [Theory, InlineData("10001"), InlineData("-1"), InlineData("abc")]
    public void Tax_rate_must_be_0_to_10000(string value)
    {
        var e = Assert.ThrowsAny<Exception>(() => Build("api", new() { ["Order:TaxRateBasisPoints"] = value }));
        Assert.Contains("Order:TaxRateBasisPoints", e.Message);
    }

    [Fact]
    public void Upstream_urls_must_be_absolute()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", new() { ["Order:Payment"] = "payment" }));
        Assert.Contains("Order:Payment", e.Message);
    }

    // P-6: on Unix "/v1" parses as the absolute URL file:///v1.
    [Theory]
    [InlineData("Order:Catalog", "/v1"), InlineData("Order:Inventory", "/v1"), InlineData("Order:Launch", "/v1"), InlineData("Order:Payment", "/v1"), InlineData("Order:Fulfillment", "/v1")]
    [InlineData("Order:Payment", "ftp://payment.test")]
    public void P6_relative_v1_base_url_rejected(string key, string value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", new() { [key] = value }));
        Assert.Contains(key, e.Message);
        Assert.DoesNotContain(value, e.Message);
    }

    public static TheoryData<string, StatusCode> ReasonTable() => new()
    {
        { "CART_EMPTY", StatusCode.FailedPrecondition }, { "SKU_NOT_BUYABLE", StatusCode.FailedPrecondition },
        { "LAUNCH_ITEMS_IN_CART", StatusCode.InvalidArgument }, { "MIXED_LAUNCH_ORDER", StatusCode.InvalidArgument },
        { "QUANTITY_LIMIT", StatusCode.InvalidArgument }, { "WARRANTY_NOT_ELIGIBLE", StatusCode.InvalidArgument },
        { "ADDRESS_INVALID", StatusCode.InvalidArgument }, { "COUPON_NOT_FOUND", StatusCode.FailedPrecondition },
        { "COUPON_INACTIVE", StatusCode.FailedPrecondition }, { "COUPON_NOT_STARTED", StatusCode.FailedPrecondition },
        { "COUPON_EXPIRED", StatusCode.FailedPrecondition }, { "COUPON_MIN_SPEND", StatusCode.FailedPrecondition },
        { "COUPON_NOT_APPLICABLE", StatusCode.FailedPrecondition }, { "COUPON_LIMIT_REACHED", StatusCode.FailedPrecondition },
        { "CANCEL_NOT_ALLOWED", StatusCode.FailedPrecondition }, { "ALREADY_SHIPPED", StatusCode.FailedPrecondition },
        { "COUPON_CODE_TAKEN", StatusCode.AlreadyExists }, { "CHECKOUT_CLOSED", StatusCode.FailedPrecondition },
        { "NOT_FOUND", StatusCode.NotFound }, { "VALIDATION_FAILED", StatusCode.InvalidArgument },
        { "ORDER_TOTAL_TOO_LOW", StatusCode.FailedPrecondition },
    };

    [Theory, MemberData(nameof(ReasonTable))]
    public void Each_reason_has_its_status(string reason, StatusCode code)
    {
        var e = OrderErrors.Error(reason, "m");

        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Rpc.Info(e).Reason);
        Assert.Equal("bloom", Rpc.Info(e).Domain);
    }

    [Fact]
    public void Validation_address_and_not_found_helpers()
    {
        var v = OrderErrors.Validation("order_id", "m");
        Assert.Equal(StatusCode.InvalidArgument, v.StatusCode);
        Assert.Equal("VALIDATION_FAILED", Rpc.Info(v).Reason);
        Assert.Equal("order_id", Rpc.Info(v).Metadata["field"]);

        var a = OrderErrors.Address("zip", "m");
        Assert.Equal(StatusCode.InvalidArgument, a.StatusCode);
        Assert.Equal("ADDRESS_INVALID", Rpc.Info(a).Reason);
        Assert.Equal("zip", Rpc.Info(a).Metadata["field"]);

        Assert.Equal(StatusCode.NotFound, OrderErrors.NotFound("The order").StatusCode);
    }

    [Fact]
    public void Ids_and_paging()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Inputs.Id(id.ToString(), "f"));
        Assert.Equal("f", Rpc.Info(Assert.Throws<RpcException>(() => Inputs.Id("x", "f"))).Metadata["field"]);

        Assert.Equal((0, 50), Inputs.Paging(0, 0));
        Assert.Equal((200, 100), Inputs.Paging(3, 100));
        Assert.Equal("page_size", Rpc.Info(Assert.Throws<RpcException>(() => Inputs.Paging(1, 101))).Metadata["field"]);
        Assert.Equal("page", Rpc.Info(Assert.Throws<RpcException>(() => Inputs.Paging(-1, 1))).Metadata["field"]);
    }

    [Fact]
    public void Role_checks()
    {
        var manager = new Caller(Guid.NewGuid(), "staff", "catalog_manager");
        var support = manager with { Role = "support" };
        var admin = manager with { Role = "admin" };
        var customer = new Caller(Guid.NewGuid(), "customer", null);

        Assert.Same(manager, manager.RequireStaff(Caller.CouponEditors));
        Assert.Same(admin, admin.RequireStaff(Caller.CouponEditors));
        Assert.Same(admin, admin.RequireStaff(Caller.SettingsEditors));
        Assert.Same(support, support.RequireStaff(Caller.OrderStaff));
        Assert.Same(admin, admin.RequireStaff(Caller.OrderStaff));
        Assert.Same(customer, customer.RequireCustomer());
        foreach (var e in new[]
        {
            Assert.Throws<RpcException>(() => manager.RequireStaff(Caller.OrderStaff)),
            Assert.Throws<RpcException>(() => manager.RequireStaff(Caller.SettingsEditors)),
            Assert.Throws<RpcException>(() => support.RequireStaff(Caller.CouponEditors)),
            Assert.Throws<RpcException>(() => customer.RequireStaff(Caller.OrderStaff)),
            Assert.Throws<RpcException>(() => manager.RequireCustomer()),
        })
        {
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
            Assert.Equal(BloomErrors.RoleNotAllowed, Rpc.Info(e).Reason);
        }
    }

    private static ShipTo Address(Action<ShipTo>? edit = null)
    {
        var a = new ShipTo { Name = "Ada Lovelace", City = "Reno", State = "NV", Zip = "89501", Phone = "+17755550100", Lines = { "1 Main St", "Apt 2" } };
        edit?.Invoke(a);
        return a;
    }

    [Fact]
    public void Address_rules()
    {
        var ok = Inputs.ShipTo(Address(a => a.Name = " Ada Lovelace "));
        Assert.Equal(("Ada Lovelace", "Reno", "NV", "89501", "+17755550100"), (ok.Name, ok.City, ok.State, ok.Zip, ok.Phone));
        Assert.Equal(["1 Main St", "Apt 2"], ok.Lines);
        Assert.Equal("89501-1234", Inputs.ShipTo(Address(a => a.Zip = "89501-1234")).Zip);
        Assert.Equal("PR", Inputs.ShipTo(Address(a => a.State = "PR")).State);

        var bad = new (string Field, Action<ShipTo> Edit)[]
        {
            ("name", a => a.Name = ""), ("name", a => a.Name = "  "), ("name", a => a.Name = new string('x', 101)),
            ("lines", a => a.Lines.Clear()), ("lines", a => a.Lines.Add("3")), ("lines", a => a.Lines[0] = ""), ("lines", a => a.Lines[1] = new string('x', 101)),
            ("city", a => a.City = ""), ("city", a => a.City = new string('x', 101)),
            ("state", a => a.State = ""), ("state", a => a.State = "XX"), ("state", a => a.State = "nv"),
            ("zip", a => a.Zip = "8950"), ("zip", a => a.Zip = "89501-12"), ("zip", a => a.Zip = "abcde"),
            ("phone", a => a.Phone = "7755550100"), ("phone", a => a.Phone = "+0755550100"), ("phone", a => a.Phone = "+1234567890123456"), ("phone", a => a.Phone = ""),
        };
        foreach (var (field, edit) in bad)
        {
            var info = Rpc.Info(Assert.Throws<RpcException>(() => Inputs.ShipTo(Address(edit))));
            Assert.Equal("ADDRESS_INVALID", info.Reason);
            Assert.Equal(field, info.Metadata["field"]);
        }
    }

    [Fact]
    public void Coupon_codes()
    {
        Assert.Equal(("SAVE-10", "save-10"), Inputs.CouponCode(" save-10 ", "coupon_code"));
        foreach (var bad in new[] { "", "ab", new string('a', 33), "save 10", "save_10" })
        {
            var info = Rpc.Info(Assert.Throws<RpcException>(() => Inputs.CouponCode(bad, "code")));
            Assert.Equal("VALIDATION_FAILED", info.Reason);
            Assert.Equal("code", info.Metadata["field"]);
        }
    }

    private static string[] Names(object payload) => JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web).EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void Contracts_serialize_to_the_documented_fields()
    {
        Assert.Equal(["orderId", "orderNumber", "accountId", "reason"], Names(new OrderCancelledV1(Guid.NewGuid(), "BLM-1", Guid.NewGuid(), "customer")));
        Assert.Equal(["orderId", "paymentId", "amountCents", "succeededAt"], Names(new PaymentSucceededV1(Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow)));
        Assert.Equal(["orderId", "reason"], Names(new ReleaseAllocationHoldV1(Guid.NewGuid(), "expired")));
    }

    [Fact]
    public async Task Checkout_metric_result_is_one_of_the_documented_values()
    {
        await using var t = await TestOrder.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var order = t.Services.GetRequiredService<OrderMetrics>();

        foreach (var result in new[] { "ok", "SOLD_OUT", "LIMIT_EXCEEDED", "COUPON_MIN_SPEND", "COUPON_CODE_TAKEN", "UNAVAILABLE" })
            order.Checkout("regular", result);

        Assert.Equal(1, metrics.Sum("checkout_total", ("kind", "regular"), ("result", "ok")));
        Assert.Equal(1, metrics.Sum("checkout_total", ("result", "sold_out")));
        Assert.Equal(1, metrics.Sum("checkout_total", ("result", "limit_exceeded")));
        Assert.Equal(1, metrics.Sum("checkout_total", ("result", "coupon_min_spend")));
        Assert.Equal(2, metrics.Sum("checkout_total", ("result", "failed")));
    }

    [Fact]
    public async Task Api_host_starts()
    {
        await using var t = await TestOrder.StartAsync(containers);

        var info = await Rpc.FailsAsync(t.Rpc.Internal.HasUnsettledOrdersAsync(new() { AccountId = "x" }, cancellationToken: TestContext.Current.CancellationToken), "VALIDATION_FAILED");
        Assert.Equal("account_id", info.Metadata["field"]);
    }
}
