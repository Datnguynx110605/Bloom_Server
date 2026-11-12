using Bloom.Payment.Contracts;
using Bloom.Payment.Context;
using Bloom.Payment.Errors;
using Bloom.Shared.Errors;
using Bloom.Shared.Messaging;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Payment.Tests;

public class ShellTests(Containers containers)
{
    private static readonly Dictionary<string, string?> Valid = TestPayment.DefaultConfig("Host=localhost;Database=unused", "amqp://localhost:5672");

    private static WebApplication Build(string mode, Dictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>(Valid) { ["Payment:Mode"] = mode };
        foreach (var (key, value) in overrides ?? []) config[key] = value;
        return PaymentApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
    }

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
        Assert.Contains("Payment:Mode", e.Message);
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

    [Theory, InlineData("Payment:Stripe:BaseUrl"), InlineData("Payment:StorefrontBaseUrl")]
    public void Urls_must_be_absolute(string key)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", new() { [key] = "stripe" }));
        Assert.Contains(key, e.Message);
    }

    // P-6: on Unix "/v1" parses as the absolute URL file:///v1.
    [Theory]
    [InlineData("Payment:Stripe:BaseUrl", "/v1"), InlineData("Payment:StorefrontBaseUrl", "/v1"), InlineData("Payment:Stripe:BaseUrl", "ftp://stripe.test")]
    public void P6_relative_v1_base_url_rejected(string key, string value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", new() { [key] = value }));
        Assert.Contains(key, e.Message);
        Assert.DoesNotContain(value, e.Message);
    }

    public static TheoryData<string, StatusCode> ReasonTable() => new()
    {
        { "PAYMENT_EXISTS_DIFFERENT_AMOUNT", StatusCode.InvalidArgument }, { "PAYMENT_CREATE_IN_PROGRESS", StatusCode.Aborted },
        { "PROVIDER_UNAVAILABLE", StatusCode.Unavailable }, { "VALIDATION_FAILED", StatusCode.InvalidArgument },
        { "NOT_FOUND", StatusCode.NotFound }, { "ROLE_NOT_ALLOWED", StatusCode.PermissionDenied },
    };

    [Theory, MemberData(nameof(ReasonTable))]
    public void Each_reason_has_its_status(string reason, StatusCode code)
    {
        var e = PaymentErrors.Error(reason, "m");

        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Rpc.Info(e).Reason);
        Assert.Equal("bloom", Rpc.Info(e).Domain);
    }

    [Fact]
    public void Validation_and_not_found_helpers()
    {
        var v = PaymentErrors.Validation("order_id", "m");
        Assert.Equal(StatusCode.InvalidArgument, v.StatusCode);
        Assert.Equal("VALIDATION_FAILED", Rpc.Info(v).Reason);
        Assert.Equal("order_id", Rpc.Info(v).Metadata["field"]);

        Assert.Equal(StatusCode.NotFound, PaymentErrors.NotFound("The payment").StatusCode);
    }

    [Fact]
    public void Role_checks()
    {
        var warehouse = new Caller(Guid.NewGuid(), "staff", "warehouse");
        var support = warehouse with { Role = "support" };
        var admin = warehouse with { Role = "admin" };

        Assert.Same(support, support.RequireStaff(Caller.Staff));
        Assert.Same(admin, admin.RequireStaff(Caller.Staff));
        foreach (var e in new[]
        {
            Assert.Throws<RpcException>(() => warehouse.RequireStaff(Caller.Staff)),
            Assert.Throws<RpcException>(() => (warehouse with { Role = "catalog_manager" }).RequireStaff(Caller.Staff)),
            Assert.Throws<RpcException>(() => new Caller(Guid.NewGuid(), "customer", null).RequireStaff(Caller.Staff)),
        })
        {
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
            Assert.Equal(BloomErrors.RoleNotAllowed, Rpc.Info(e).Reason);
        }
    }

    [Fact]
    public async Task Contracts_serialize_to_the_documented_fields()
    {
        await using var t = await TestPayment.StartAsync(containers);
        await using var scope = t.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.PaymentDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<OutboxWriter>();
        var order = Guid.NewGuid();

        outbox.PublishEvent(PaymentMessages.Succeeded, new PaymentSucceededV1(order, Guid.NewGuid(), 1, DateTimeOffset.UtcNow));
        outbox.PublishEvent(PaymentMessages.RefundCompleted, new RefundCompletedV1(order, Guid.NewGuid(), 1, null));
        outbox.PublishEvent(PaymentMessages.RefundFailed, new RefundFailedV1(order, Guid.NewGuid(), "expired_or_canceled_card"));

        // The envelope is the published message; its payload is what BloomJson wrote.
        var payloads = db.ChangeTracker.Entries<Shared.Persistence.OutboxMessage>()
            .ToDictionary(e => e.Entity.MessageType, e => Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(e.Entity.Payload)).Payload);
        string[] Names(string type) => payloads[type].EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["orderId", "paymentId", "amountCents", "succeededAt"], Names(PaymentMessages.Succeeded));
        Assert.Equal(["orderId", "refundId", "amountCents", "lines"], Names(PaymentMessages.RefundCompleted));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, payloads[PaymentMessages.RefundCompleted].GetProperty("lines").ValueKind);
        Assert.Equal(["orderId", "refundId", "failureReason"], Names(PaymentMessages.RefundFailed));
    }

    [Fact]
    public async Task Api_host_starts()
    {
        await using var t = await TestPayment.StartAsync(containers);

        var info = await Rpc.FailsAsync(t.Internal.CreatePaymentAsync(new() { OrderId = "x" }, cancellationToken: TestContext.Current.CancellationToken), "VALIDATION_FAILED");
        Assert.Equal("order_id", info.Metadata["field"]);
    }
}
