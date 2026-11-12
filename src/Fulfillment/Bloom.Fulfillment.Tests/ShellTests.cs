using System.Text.Json;
using Bloom.Fulfillment.Context;
using Bloom.Fulfillment.Errors;
using Bloom.Order.Contracts;
using Bloom.Payment.Contracts;
using Bloom.Shared.Errors;
using Bloom.Shared.Messaging;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Bloom.Fulfillment.Tests;

public class ShellTests(Containers containers)
{
    private static readonly Dictionary<string, string?> Valid = TestFulfillment.DefaultConfig("Host=localhost;Database=unused", "amqp://localhost:5672", "http://localhost:8080");

    private static WebApplication Build(string mode, Dictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>(Valid) { ["Fulfillment:Mode"] = mode };
        foreach (var (key, value) in overrides ?? []) config[key] = value;
        return FulfillmentApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
    }

    [Fact]
    public void Unknown_mode_fails_at_build()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("edge"));
        Assert.Contains("Fulfillment:Mode", e.Message);
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

    [Theory, InlineData("Fulfillment:Parcel:WeightOz"), InlineData("Fulfillment:Parcel:LengthIn")]
    public void Parcel_sizes_must_be_positive(string key)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("api", new() { [key] = "-1" }));
        Assert.Contains(key, e.Message);
    }

    public static TheoryData<string, StatusCode> ReasonTable() => new()
    {
        { "PACKING_STARTED", StatusCode.FailedPrecondition }, { "RETURN_QUANTITY_EXCEEDED", StatusCode.InvalidArgument },
        { "ALREADY_SHIPPED", StatusCode.FailedPrecondition }, { "INVALID_RETURN_STATE", StatusCode.FailedPrecondition },
        { "SERIALS_INCOMPLETE", StatusCode.FailedPrecondition }, { "PARTIAL_EXCEEDS_SHARE", StatusCode.InvalidArgument },
        { "LABEL_NOT_READY", StatusCode.FailedPrecondition }, { "RESHIP_NO_STOCK", StatusCode.FailedPrecondition },
        { "RETURN_WINDOW_CLOSED", StatusCode.FailedPrecondition }, { "INVALID_STATE", StatusCode.FailedPrecondition },
        { "NOT_FOUND", StatusCode.NotFound }, { "VALIDATION_FAILED", StatusCode.InvalidArgument },
    };

    [Theory, MemberData(nameof(ReasonTable))]
    public void Each_reason_has_its_status(string reason, StatusCode code)
    {
        var e = FulfillmentErrors.Error(reason, "m");

        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Rpc.Info(e).Reason);
        Assert.Equal("bloom", Rpc.Info(e).Domain);
    }

    [Fact]
    public void Validation_and_not_found_helpers()
    {
        var v = FulfillmentErrors.Validation("order_id", "m");
        Assert.Equal(StatusCode.InvalidArgument, v.StatusCode);
        Assert.Equal("order_id", Rpc.Info(v).Metadata["field"]);

        Assert.Equal(StatusCode.NotFound, FulfillmentErrors.NotFound("The task").StatusCode);
    }

    [Fact]
    public void Inputs_parse_ids_serials_and_paging()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Inputs.Id(id.ToString(), "f"));
        Assert.Equal("VALIDATION_FAILED", Rpc.Info(Assert.Throws<RpcException>(() => Inputs.Id("x", "f"))).Reason);

        Assert.Equal("AB12", Inputs.Serial(" ab12 ", "f"));
        foreach (var bad in new[] { "", "a-b", new string('A', 33) })
            Assert.Equal("f", Rpc.Info(Assert.Throws<RpcException>(() => Inputs.Serial(bad, "f"))).Metadata["field"]);

        Assert.Equal((0, 50), Inputs.Paging(0, 0));
        Assert.Equal((200, 100), Inputs.Paging(3, 100));
        Assert.Equal("page_size", Rpc.Info(Assert.Throws<RpcException>(() => Inputs.Paging(1, 101))).Metadata["field"]);
        Assert.Equal("page", Rpc.Info(Assert.Throws<RpcException>(() => Inputs.Paging(-1, 1))).Metadata["field"]);
    }

    [Fact]
    public void Role_checks()
    {
        var warehouse = new Caller(Guid.NewGuid(), "staff", "warehouse");
        var support = warehouse with { Role = "support" };
        var customer = new Caller(Guid.NewGuid(), "customer", null);

        Assert.Same(warehouse, warehouse.RequireStaff(Caller.Packers));
        Assert.Same(support, support.RequireStaff(Caller.Deciders));
        Assert.Same(support, support.RequireStaff(Caller.ReturnViewers));
        Assert.Same(warehouse, warehouse.RequireStaff(Caller.ReturnViewers));
        Assert.Same(customer, customer.RequireCustomer());
        foreach (var e in new[]
        {
            Assert.Throws<RpcException>(() => warehouse.RequireStaff(Caller.Deciders)),
            Assert.Throws<RpcException>(() => support.RequireStaff(Caller.Packers)),
            Assert.Throws<RpcException>(() => customer.RequireStaff(Caller.ReturnViewers)),
            Assert.Throws<RpcException>(() => warehouse.RequireCustomer()),
        })
        {
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
            Assert.Equal(BloomErrors.RoleNotAllowed, Rpc.Info(e).Reason);
        }
    }

    private static JsonElement Json(object payload) => JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);

    private static T RoundTrip<T>(string type, T payload) =>
        Envelope.Parse(new Envelope(Guid.NewGuid(), type, MessageKinds.Event, "test", DateTime.UtcNow, Guid.NewGuid(), Json(payload!)).ToUtf8Bytes())
            .GetPayload<T>();

    [Fact]
    public void Payloads_serialize_to_the_documented_fields()
    {
        var refund = Json(new RefundPaymentV1(Guid.NewGuid(), Guid.NewGuid(), 500, "return", [new RefundLineV1(1, 500)]));
        Assert.Equal(["orderId", "refundId", "amountCents", "reason", "lines"], refund.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["lineNo", "amountCents"], refund.GetProperty("lines")[0].EnumerateObject().Select(p => p.Name));

        var ready = new OrderReadyToFulfillV1(
            Guid.NewGuid(), Guid.NewGuid(), "BL-1", new ShipToV1("Ada", ["1 Main St", "Apt 2"], "Reno", "NV", "89501", "+17755550100"),
            "standard", 799, 12999,
            [
                new ReadyLineV1(1, "product", Guid.NewGuid(), Guid.NewGuid(), true, 1, null, null, 10000, 800),
                new ReadyLineV1(2, "warranty_plan", Guid.NewGuid(), Guid.NewGuid(), false, 1, 1, 24, 2000, 100),
            ]);
        var back = RoundTrip(OrderMessages.ReadyToFulfill, ready);
        Assert.Equal(ready.OrderId, back.OrderId);
        Assert.Equal(ready.ShipTo.Lines, back.ShipTo.Lines);
        Assert.Equal(ready.Lines.Select(l => (l.LineNo, l.ParentLineNo, l.CoverageMonths, l.PaidCents)), back.Lines.Select(l => (l.LineNo, l.ParentLineNo, l.CoverageMonths, l.PaidCents)));

        var noLines = new RefundPaymentV1(Guid.NewGuid(), Guid.NewGuid(), 1, "return", null);
        Assert.Null(RoundTrip(PaymentMessages.RefundPayment, noLines).Lines);
    }

    [Fact]
    public async Task Api_host_starts()
    {
        await using var t = await TestFulfillment.StartAsync(containers);

        await Rpc.FailsAsync(t.Internal.CancelPackTaskAsync(new() { OrderId = "x", By = "customer" }, cancellationToken: TestContext.Current.CancellationToken), "VALIDATION_FAILED");
        var by = await Rpc.FailsAsync(t.Internal.CancelPackTaskAsync(new() { OrderId = Guid.NewGuid().ToString(), By = "bob" }, cancellationToken: TestContext.Current.CancellationToken), "VALIDATION_FAILED");
        Assert.Equal("by", by.Metadata["field"]);
    }
}
