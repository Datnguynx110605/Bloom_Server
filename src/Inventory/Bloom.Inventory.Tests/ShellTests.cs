using Bloom.Inventory.Context;
using Bloom.Inventory.Errors;
using Bloom.Shared.Errors;
using Google.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Status = Google.Rpc.Status;

namespace Bloom.Inventory.Tests;

public class ShellTests(Containers containers)
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["Inventory:Database"] = "Host=localhost;Database=unused",
        ["Inventory:RabbitMq"] = "amqp://localhost:5672",
        ["Inventory:Catalog"] = "http://localhost:8080",
    };

    private static WebApplication Build(string mode, Dictionary<string, string?>? overrides = null)
    {
        var config = new Dictionary<string, string?>(Valid) { ["Inventory:Mode"] = mode };
        foreach (var (key, value) in overrides ?? []) config[key] = value;
        return InventoryApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
    }

    [Fact]
    public void Unknown_mode_fails_at_build()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Build("edge"));
        Assert.Contains("Inventory:Mode", e.Message);
    }

    [Theory, InlineData("Inventory:Database"), InlineData("Inventory:RabbitMq"), InlineData("Inventory:Catalog")]
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
        { "SOLD_OUT", StatusCode.FailedPrecondition }, { "RECEIPT_INVALID", StatusCode.InvalidArgument },
        { "RESERVATION_CONFLICT", StatusCode.InvalidArgument }, { "ADJUSTMENT_EXCEEDS_AVAILABLE", StatusCode.FailedPrecondition },
        { "SKU_NOT_STOCKED", StatusCode.FailedPrecondition }, { "SERIAL_ASSIGNED", StatusCode.FailedPrecondition },
        { "SERIAL_NOT_AVAILABLE", StatusCode.FailedPrecondition }, { "NOT_FOUND", StatusCode.NotFound },
        { "SERIAL_COUNT_EXCEEDED", StatusCode.FailedPrecondition }, { "VALIDATION_FAILED", StatusCode.InvalidArgument },
        { "NOT_COMMITTED", StatusCode.FailedPrecondition }, { "CATALOG_UNAVAILABLE", StatusCode.Unavailable },
    };

    private static Status Detail(RpcException e) => e.GetRpcStatus()!;

    private static ErrorInfo Info(RpcException e) => Detail(e).Details.Single(d => d.Is(ErrorInfo.Descriptor)).Unpack<ErrorInfo>();

    [Theory, MemberData(nameof(ReasonTable))]
    public void Each_reason_has_its_status(string reason, StatusCode code)
    {
        var e = InventoryErrors.Error(reason, "m");

        Assert.Equal(code, e.StatusCode);
        Assert.Equal(reason, Info(e).Reason);
        Assert.Equal("bloom", Info(e).Domain);
    }

    [Fact]
    public void Sold_out_carries_the_sku()
    {
        var sku = Guid.NewGuid();

        var e = InventoryErrors.SoldOut(sku);

        Assert.Equal(StatusCode.FailedPrecondition, e.StatusCode);
        Assert.Equal("SOLD_OUT", Info(e).Reason);
        Assert.Equal(sku.ToString(), Info(e).Metadata["skuId"]);
    }

    [Fact]
    public void Receipt_invalid_lists_every_violation()
    {
        var e = InventoryErrors.ReceiptInvalid([("lines[0].quantity", "must be positive"), ("lines[1].serials[0]", "already in stock")]);

        Assert.Equal(StatusCode.InvalidArgument, e.StatusCode);
        Assert.Equal("RECEIPT_INVALID", Info(e).Reason);
        var violations = Detail(e).Details.Single(d => d.Is(BadRequest.Descriptor)).Unpack<BadRequest>().FieldViolations;
        Assert.Equal(["lines[0].quantity", "lines[1].serials[0]"], violations.Select(v => v.Field));
        Assert.Equal(["must be positive", "already in stock"], violations.Select(v => v.Description));
    }

    [Fact]
    public void Role_checks()
    {
        var warehouse = new Caller(Guid.NewGuid(), "staff", "warehouse");
        var support = warehouse with { Role = "support" };
        var manager = warehouse with { Role = "catalog_manager" };

        Assert.Same(warehouse, warehouse.RequireStaff(Caller.StockWriters));
        Assert.Same(support, support.RequireStaff(Caller.SerialReaders));
        Assert.Same(manager, manager.RequireStaff(Caller.StockViewers));
        foreach (var e in new[]
        {
            Assert.Throws<RpcException>(() => support.RequireStaff(Caller.StockWriters)),
            Assert.Throws<RpcException>(() => manager.RequireStaff(Caller.StockWriters)),
            Assert.Throws<RpcException>(() => manager.RequireStaff(Caller.LedgerReaders)),
            Assert.Throws<RpcException>(() => manager.RequireStaff(Caller.SerialReaders)),
            Assert.Throws<RpcException>(() => (warehouse with { UserType = "customer", Role = null }).RequireStaff(Caller.StockViewers)),
        })
        {
            Assert.Equal(StatusCode.PermissionDenied, e.StatusCode);
            Assert.Equal(BloomErrors.RoleNotAllowed, Info(e).Reason);
        }
    }

    [Fact]
    public async Task Api_host_starts_as_the_app_role()
    {
        await using var t = await TestInventory.StartAsync(containers);

        await using var scope = t.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.InventoryDbContext>();
        var user = await db.Database.SqlQuery<string>($"SELECT current_user AS \"Value\"").SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("inventory_app", user);
    }
}
