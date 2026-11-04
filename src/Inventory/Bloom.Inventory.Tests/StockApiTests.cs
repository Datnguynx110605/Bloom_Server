using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Google.Rpc;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Inventory.Tests;

public class StockApiTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StockLine Line(Guid sku, int quantity) => new() { SkuId = sku.ToString(), Quantity = quantity };

    private static ReserveStockRequest Reserve(Guid orderId, params StockLine[] lines) =>
        new() { OrderId = orderId.ToString(), Lines = { lines } };

    private static CommitForReshipRequest Reship(Guid reshipId, Guid orderId, params StockLine[] lines) =>
        new() { ReshipId = reshipId.ToString(), OrderId = orderId.ToString(), Lines = { lines } };

    private static ErrorInfo Info(RpcException e) => e.GetRpcStatus()!.Details.Single(d => d.Is(ErrorInfo.Descriptor)).Unpack<ErrorInfo>();

    /// <summary>Runs the call, which must fail with <paramref name="reason"/>; returns its <c>ErrorInfo</c>.</summary>
    private static async Task<ErrorInfo> FailsAsync(Task call, string reason)
    {
        var e = await Assert.ThrowsAsync<RpcException>(() => call);
        var info = Info(e);
        Assert.Equal("bloom", info.Domain);
        Assert.Equal(reason, info.Reason);
        return info;
    }

    private static async Task<ErrorInfo> FailsAsync<T>(Grpc.Core.AsyncUnaryCall<T> call, string reason) => await FailsAsync(call.ResponseAsync, reason);

    private static async Task<List<StockAllocation>> AllocationsAsync(TestInventory t, Guid refId)
    {
        await using var db = t.NewDb();
        return await db.StockAllocations.AsNoTracking().Where(a => a.RefId == refId).ToListAsync(Ct);
    }

    /// <summary>A SKU row with all counters zero and no receipt, for tests that start from <c>on_hand = 0</c>.</summary>
    private static async Task<Guid> EmptyStockedSkuAsync(TestInventory t)
    {
        var sku = Seed.Sku(t);
        await using var db = t.NewDb();
        db.StockItems.Add(new StockItem { SkuId = sku, ProductId = Guid.CreateVersion7(), UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        return sku;
    }

    [Fact]
    public async Task I01_500_concurrent_reserves_of_one_with_10_available()
    {
        await using var t = await TestInventory.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 10);

        var calls = Enumerable.Range(0, 500).Select(async _ =>
        {
            try
            {
                await t.Stock.ReserveStockAsync(Reserve(Guid.CreateVersion7(), Line(sku, 1)), cancellationToken: Ct);
                return (string?)null;
            }
            catch (RpcException e)
            {
                return Info(e).Reason + "|" + Info(e).Metadata["skuId"];
            }
        }).ToList();
        var results = await Task.WhenAll(calls);

        Assert.Equal(10, results.Count(r => r is null));
        Assert.All(results.Where(r => r is not null), r => Assert.Equal($"SOLD_OUT|{sku}", r));
        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((10, 10), (item.Reserved, item.OnHand));
        Assert.Equal(10, (await Seed.LedgerAsync(t, sku)).Count(e => e.EntryType == "reserved"));
        Assert.Equal(10, metrics.Sum("reserve_total", ("result", "ok")));
        Assert.Equal(490, metrics.Sum("reserve_total", ("result", "sold_out")));
        Assert.Equal(500, metrics.Count("reserve_seconds"));
    }

    [Fact]
    public async Task I02_retried_reserve_is_ok_and_reserved_once()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (a, b) = (Seed.Sku(t), Seed.Sku(t));
        await Seed.ReceiveAsync(t, a, 5);
        await Seed.ReceiveAsync(t, b, 5);
        var order = Guid.CreateVersion7();
        var request = Reserve(order, Line(a, 2), Line(b, 1));

        await t.Stock.ReserveStockAsync(request, cancellationToken: Ct);
        await t.Stock.ReserveStockAsync(Reserve(order, Line(b, 1), Line(a, 2)), cancellationToken: Ct); // same lines, other order

        Assert.Equal(2, (await Seed.ItemAsync(t, a))!.Reserved);
        Assert.Equal(1, (await Seed.ItemAsync(t, b))!.Reserved);
        Assert.Single(await Seed.LedgerAsync(t, a), e => e.EntryType == "reserved");
        Assert.Single(await Seed.LedgerAsync(t, b), e => e.EntryType == "reserved");
        var allocations = await AllocationsAsync(t, order);
        Assert.Equal(2, allocations.Count);
        Assert.All(allocations, x => Assert.Equal(("regular", "reserved", order), (x.Kind, x.Status, x.OrderId)));
        var ledger = (await Seed.LedgerAsync(t, a)).Single(e => e.EntryType == "reserved");
        Assert.Equal((2, "order", order, "inventory"), (ledger.DReserved, ledger.RefType, ledger.RefId, ledger.Actor));
        Assert.Equal(0, ledger.DOnHand);
    }

    [Fact]
    public async Task Concurrent_duplicate_reserves_apply_once()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (a, b) = (Seed.Sku(t), Seed.Sku(t));
        await Seed.ReceiveAsync(t, a, 50);
        await Seed.ReceiveAsync(t, b, 50);
        var order = Guid.CreateVersion7();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            await t.Stock.ReserveStockAsync(Reserve(order, Line(a, 2), Line(b, 3)), cancellationToken: Ct)));

        Assert.Equal((2, 3), ((await Seed.ItemAsync(t, a))!.Reserved, (await Seed.ItemAsync(t, b))!.Reserved));
        Assert.Single(await Seed.LedgerAsync(t, a), e => e.EntryType == "reserved");
        Assert.Equal(2, (await AllocationsAsync(t, order)).Count);
    }

    [Fact]
    public async Task Concurrent_duplicates_taking_the_last_units_are_all_ok()
    {
        await using var t = await TestInventory.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (a, b) = (Seed.Sku(t), Seed.Sku(t));
        await Seed.ReceiveAsync(t, a, 2);
        await Seed.ReceiveAsync(t, b, 3); // exactly one request's worth: a duplicate that loses the race finds nothing left
        var order = Guid.CreateVersion7();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            await t.Stock.ReserveStockAsync(Reserve(order, Line(a, 2), Line(b, 3)), cancellationToken: Ct)));

        Assert.Equal((2, 3), ((await Seed.ItemAsync(t, a))!.Reserved, (await Seed.ItemAsync(t, b))!.Reserved));
        Assert.Single(await Seed.LedgerAsync(t, a), e => e.EntryType == "reserved");
        Assert.Single(await Seed.LedgerAsync(t, b), e => e.EntryType == "reserved");
        Assert.Equal(20, metrics.Sum("reserve_total", ("result", "ok")));
        Assert.Equal(0, metrics.Sum("reserve_total", ("result", "sold_out")));
    }

    [Fact]
    public async Task Concurrent_reship_duplicates_taking_the_last_units_are_all_ok()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 2);
        var (reship, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            await t.Stock.CommitForReshipAsync(Reship(reship, order, Line(sku, 2)), cancellationToken: Ct)));

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((0, 2), (item.OnHand, item.Committed));
        Assert.Single(await Seed.LedgerAsync(t, sku), e => e.EntryType == "committed");
    }

    [Fact]
    public async Task Same_order_with_other_lines_is_RESERVATION_CONFLICT()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (a, b) = (Seed.Sku(t), Seed.Sku(t));
        await Seed.ReceiveAsync(t, a, 5);
        await Seed.ReceiveAsync(t, b, 5);
        var order = Guid.CreateVersion7();
        await t.Stock.ReserveStockAsync(Reserve(order, Line(a, 2)), cancellationToken: Ct);

        await FailsAsync(t.Stock.ReserveStockAsync(Reserve(order, Line(a, 3)), cancellationToken: Ct), "RESERVATION_CONFLICT");
        await FailsAsync(t.Stock.ReserveStockAsync(Reserve(order, Line(a, 2), Line(b, 1)), cancellationToken: Ct), "RESERVATION_CONFLICT");
        Assert.Equal(2, (await Seed.ItemAsync(t, a))!.Reserved);
        Assert.Equal(0, (await Seed.ItemAsync(t, b))!.Reserved);
    }

    [Fact]
    public async Task Multi_sku_reserve_is_all_or_nothing()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (a, b) = (Seed.Sku(t), Seed.Sku(t));
        await Seed.ReceiveAsync(t, a, 5);
        await Seed.ReceiveAsync(t, b, 1);
        var order = Guid.CreateVersion7();

        var info = await FailsAsync(t.Stock.ReserveStockAsync(Reserve(order, Line(a, 2), Line(b, 2)), cancellationToken: Ct), "SOLD_OUT");

        Assert.Equal(b.ToString(), info.Metadata["skuId"]);
        Assert.Equal(0, (await Seed.ItemAsync(t, a))!.Reserved);
        Assert.Equal(0, (await Seed.ItemAsync(t, b))!.Reserved);
        Assert.Empty(await AllocationsAsync(t, order));
        Assert.DoesNotContain(await Seed.LedgerAsync(t, a), e => e.EntryType == "reserved");
    }

    [Fact]
    public async Task Never_received_sku_is_SKU_NOT_STOCKED()
    {
        await using var t = await TestInventory.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var a = Seed.Sku(t);
        await Seed.ReceiveAsync(t, a, 5);

        await FailsAsync(t.Stock.ReserveStockAsync(Reserve(Guid.CreateVersion7(), Line(a, 1), Line(Guid.CreateVersion7(), 1)), cancellationToken: Ct), "SKU_NOT_STOCKED");

        Assert.Equal(0, (await Seed.ItemAsync(t, a))!.Reserved);
        Assert.Equal(1, metrics.Sum("reserve_total", ("result", "sold_out")));
    }

    [Fact]
    public async Task Invalid_reserve_requests_are_VALIDATION_FAILED()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var order = Guid.CreateVersion7();

        async Task Check(ReserveStockRequest request, string field)
        {
            var info = await FailsAsync(t.Stock.ReserveStockAsync(request, cancellationToken: Ct), "VALIDATION_FAILED");
            Assert.Equal(field, info.Metadata["field"]);
        }

        await Check(Reserve(order), "lines");
        await Check(Reserve(order, Line(sku, 0)), "quantity");
        await Check(Reserve(order, Line(sku, -1)), "quantity");
        await Check(Reserve(order, Line(sku, 1), Line(sku, 2)), "lines");
        await Check(Reserve(order, new StockLine { SkuId = "nope", Quantity = 1 }), "sku_id");
        await Check(new ReserveStockRequest { OrderId = "nope", Lines = { Line(sku, 1) } }, "order_id");
        await Check(new ReserveStockRequest { Lines = { Line(sku, 1) } }, "order_id");
        Assert.Equal(0, (await Seed.ItemAsync(t, sku))!.Reserved);
    }

    [Fact]
    public async Task I06_receipt_during_100_concurrent_reserves_serves_backorders_first()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = await EmptyStockedSkuAsync(t);
        var backorder = await Seed.BackorderAsync(t, sku, 3, DateTimeOffset.UtcNow.AddMinutes(-5));

        var receipt = Seed.ReceiveAsync(t, sku, 10);
        var reserves = Enumerable.Range(0, 100).Select(async _ =>
        {
            try
            {
                await t.Stock.ReserveStockAsync(Reserve(Guid.CreateVersion7(), Line(sku, 1)), cancellationToken: Ct);
                return true;
            }
            catch (RpcException e) when (Info(e).Reason is "SOLD_OUT" or "SKU_NOT_STOCKED")
            {
                return false;
            }
        }).ToList();
        var ok = (await Task.WhenAll(reserves)).Count(r => r);
        await receipt;

        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal(3, item.Committed);
        Assert.Equal("committed", (await AllocationsAsync(t, backorder)).Single().Status);
        Assert.InRange(ok, 0, 7);
        Assert.Equal(ok, item.Reserved);
        Assert.True(item.OnHand - item.Reserved >= 0);
        Assert.Equal(7, item.OnHand);
    }

    [Fact]
    public async Task Opposite_lock_orders_never_deadlock()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (a, b) = (Seed.Sku(t), Seed.Sku(t));
        await Seed.ReceiveAsync(t, a, 1000);
        await Seed.ReceiveAsync(t, b, 1000);
        var staff = TestCaller.Staff("warehouse");

        async Task<string?> Run(Func<Task> call)
        {
            try { await call(); return null; }
            catch (RpcException e) { return Info(e).Reason; }
        }

        var failures = new List<string>();
        for (var round = 0; round < 50; round++)
        {
            var results = await Task.WhenAll(
                Run(async () => await t.Stock.ReserveStockAsync(Reserve(Guid.CreateVersion7(), Line(b, 1), Line(a, 1)), cancellationToken: Ct)),
                Run(async () => await t.Stock.ReserveStockAsync(Reserve(Guid.CreateVersion7(), Line(a, 1), Line(b, 1)), cancellationToken: Ct)),
                Run(async () =>
                {
                    var body = new { lines = new[] { new { skuId = b, quantity = 1 }, new { skuId = a, quantity = 1 } } };
                    using var response = await t.SendAsync(HttpMethod.Post, "/api/admin/inventory/receipts", body, staff, Guid.NewGuid());
                    Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                }));
            failures.AddRange(results.OfType<string>());
        }

        Assert.All(failures, f => Assert.Equal("SOLD_OUT", f));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Message.Contains("40P01") || e.Message.Contains("deadlock", StringComparison.OrdinalIgnoreCase));
        var item = (await Seed.ItemAsync(t, a))!;
        Assert.Equal(item.Reserved, (await Seed.ItemAsync(t, b))!.Reserved);
    }

    [Fact]
    public async Task GetAvailability_returns_available_per_sku()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var a = Seed.Sku(t);
        var unknown = Guid.CreateVersion7();
        await Seed.ReceiveAsync(t, a, 5);
        await t.Stock.ReserveStockAsync(Reserve(Guid.CreateVersion7(), Line(a, 2)), cancellationToken: Ct);

        var response = await t.Stock.GetAvailabilityAsync(
            new GetAvailabilityRequest { SkuIds = { a.ToString(), unknown.ToString(), a.ToString() } }, cancellationToken: Ct);

        Assert.Equal([(a.ToString(), 3), (unknown.ToString(), 0)], response.Items.Select(i => (i.SkuId, i.Available)));
        Assert.Empty((await t.Stock.GetAvailabilityAsync(new GetAvailabilityRequest(), cancellationToken: Ct)).Items);
        var info = await FailsAsync(t.Stock.GetAvailabilityAsync(new GetAvailabilityRequest { SkuIds = { a.ToString(), "nope" } }, cancellationToken: Ct), "VALIDATION_FAILED");
        Assert.Equal("sku_ids", info.Metadata["field"]);
    }

    [Fact]
    public async Task I16_reship_without_stock_is_SOLD_OUT()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 2);
        var reship = Guid.CreateVersion7();
        await t.Stock.ReserveStockAsync(Reserve(Guid.CreateVersion7(), Line(sku, 2)), cancellationToken: Ct); // everything is promised: available 0

        var info = await FailsAsync(t.Stock.CommitForReshipAsync(Reship(reship, Guid.CreateVersion7(), Line(sku, 1)), cancellationToken: Ct), "SOLD_OUT");

        Assert.Equal(sku.ToString(), info.Metadata["skuId"]);
        Assert.Empty(await AllocationsAsync(t, reship));
        var item = (await Seed.ItemAsync(t, sku))!;
        Assert.Equal((2, 0), (item.OnHand, item.Committed));
    }

    [Fact]
    public async Task Reship_commits_stock_idempotently()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var (a, b) = (Seed.Sku(t), Seed.Sku(t));
        await Seed.ReceiveAsync(t, a, 5);
        await Seed.ReceiveAsync(t, b, 5);
        var (reship, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        await t.Stock.CommitForReshipAsync(Reship(reship, order, Line(a, 2), Line(b, 1)), cancellationToken: Ct);
        await t.Stock.CommitForReshipAsync(Reship(reship, order, Line(b, 1), Line(a, 2)), cancellationToken: Ct); // retry

        var item = (await Seed.ItemAsync(t, a))!;
        Assert.Equal((3, 2, 0), (item.OnHand, item.Committed, item.Reserved));
        var allocation = (await AllocationsAsync(t, reship)).Single(x => x.SkuId == a);
        Assert.Equal(("reship", "committed", order, 2), (allocation.Kind, allocation.Status, allocation.OrderId, allocation.Quantity));
        var ledger = (await Seed.LedgerAsync(t, a)).Single(e => e.EntryType == "committed");
        Assert.Equal((-2, 2, "reship", reship), (ledger.DOnHand, ledger.DCommitted, ledger.RefType, ledger.RefId));
        Assert.Single(await Seed.LedgerAsync(t, b), e => e.EntryType == "committed");

        await FailsAsync(t.Stock.CommitForReshipAsync(Reship(reship, order, Line(a, 3)), cancellationToken: Ct), "RESERVATION_CONFLICT");
        await FailsAsync(t.Stock.CommitForReshipAsync(Reship(Guid.CreateVersion7(), order, Line(a, 1), Line(Guid.CreateVersion7(), 1)), cancellationToken: Ct), "SKU_NOT_STOCKED");
        Assert.Equal(3, (await Seed.ItemAsync(t, a))!.OnHand);
    }

    [Fact]
    public async Task Invalid_reship_requests_are_VALIDATION_FAILED()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var sku = Seed.Sku(t);
        await Seed.ReceiveAsync(t, sku, 5);
        var id = Guid.CreateVersion7();

        async Task Check(CommitForReshipRequest request, string field) =>
            Assert.Equal(field, (await FailsAsync(t.Stock.CommitForReshipAsync(request, cancellationToken: Ct), "VALIDATION_FAILED")).Metadata["field"]);

        await Check(new() { OrderId = id.ToString(), Lines = { Line(sku, 1) } }, "reship_id");
        await Check(new() { ReshipId = id.ToString(), Lines = { Line(sku, 1) } }, "order_id");
        await Check(Reship(id, id), "lines");
        await Check(Reship(id, id, Line(sku, 0)), "quantity");
    }

    [Fact]
    public async Task Internal_methods_have_no_http_route()
    {
        await using var t = await TestInventory.StartAsync(containers);

        using var response = await t.SendAsync(HttpMethod.Post, "/api/admin/inventory/reserve", new { orderId = Guid.NewGuid(), lines = Array.Empty<object>() }, TestCaller.Staff("admin"));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}
