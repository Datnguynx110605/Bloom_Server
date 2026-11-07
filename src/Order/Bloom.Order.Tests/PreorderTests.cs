using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Consumers;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Payment.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Tests;

public class PreorderTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Task ReleaseAsync(TestOrder t, Guid launchId, string stage = "released") =>
        Consume.Async<LaunchStageChangedConsumer>(t, LaunchMessages.StageChanged, new LaunchStageChangedV1(launchId, stage, t.Clock.GetUtcNow(), t.Clock.GetUtcNow(), [], []));

    private static Task AllocatedAsync(TestOrder t, Guid orderId) =>
        Consume.Async<StockAllocatedConsumer>(t, InventoryMessages.StockAllocated, new StockAllocatedV1(orderId));

    private static Task PaidAsync(TestOrder t, Guid orderId) =>
        Consume.Async<PaymentSucceededConsumer>(t, PaymentMessages.Succeeded, new PaymentSucceededV1(orderId, FakeUpstreams.PaymentIdOf(orderId.ToString("D")), 0, t.Clock.GetUtcNow()));

    /// <summary>A launch checkout left at <c>pending_payment</c>; <paramref name="paid"/> pays it, which leaves it <c>awaiting_stock</c>.</summary>
    private static async Task<Guid> LaunchOrderAsync(TestOrder t, Guid launchId, bool paid)
    {
        var device = Seed.LaunchSku(t.Upstreams, launchId, priceCents: 1000);
        var response = await Seed.CheckoutAsync(t, Guid.NewGuid(), configure: r =>
            r.Launch = new LaunchSource { LaunchId = launchId.ToString("D"), PassToken = "pass-1", Lines = { new CheckoutLine { Kind = "product", SkuId = device.SkuId, Quantity = 1 } } });
        var id = Guid.Parse(response.OrderId);
        if (paid) await PaidAsync(t, id);
        return id;
    }

    /// <summary>
    /// <paramref name="count"/> copies of the order <paramref name="template"/> in one statement (generate_series), moved to <paramref name="launchId"/>, <paramref name="status"/>
    /// and <paramref name="allocated"/>, with their own ids and numbers.
    /// </summary>
    private static async Task<int> CloneAsync(TestOrder t, Guid template, Guid launchId, int count, string status, bool allocated)
    {
        await using var db = t.NewDb();
        var columns = await db.Database.SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'orders' ORDER BY ordinal_position").ToListAsync(Ct);
        string Pick(string c) => c switch
        {
            "id" => "gen_random_uuid()",
            "number" => "'T' || left(md5(random()::text || g::text), 11)",
            "launch_id" => "@launch",
            "status" => "@status",
            "stock_allocated" => "@allocated",
            "launch_released" => "false",
            "handed_to_fulfillment" => "false",
            _ => c,
        };
#pragma warning disable EF1003 // the column names come from information_schema, the values are parameters
        return await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO orders (" + string.Join(", ", columns) + ") SELECT " + string.Join(", ", columns.Select(Pick)) + " FROM orders, generate_series(1, @count) g WHERE id = @template",
            [new Npgsql.NpgsqlParameter("launch", launchId), new Npgsql.NpgsqlParameter("status", status), new Npgsql.NpgsqlParameter("allocated", allocated),
                new Npgsql.NpgsqlParameter("count", count), new Npgsql.NpgsqlParameter("template", template)], Ct);
#pragma warning restore EF1003
    }

    private static async Task<OrderRow> OrderAsync(TestOrder t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id, Ct);
    }

    private static async Task<List<(string? From, string To, string Actor)>> HistoryAsync(TestOrder t, Guid orderId)
    {
        await using var db = t.NewDb();
        return (await db.OrderStatusHistory.AsNoTracking().Where(h => h.OrderId == orderId).OrderBy(h => h.Id).ToListAsync(Ct)).Select(h => (h.FromStatus, h.ToStatus, h.ActorType)).ToList();
    }

    private static async Task<Dictionary<string, int>> CountsAsync(TestOrder t, Guid launchId)
    {
        await using var db = t.NewDb();
        return await db.Orders.AsNoTracking().Where(o => o.LaunchId == launchId).GroupBy(o => o.Status).ToDictionaryAsync(g => g.Key, g => g.Count(), Ct);
    }

    /// <summary>Events per order id: the largest count must be 1.</summary>
    private static async Task<(int Orders, int MostPerOrder)> PerOrderAsync(TestOrder t, string messageType)
    {
        await using var db = t.NewDb();
        var perOrder = await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM outbox_messages WHERE message_type = @type GROUP BY payload -> 'payload' ->> 'orderId'",
            new Npgsql.NpgsqlParameter("type", messageType)).ToListAsync(Ct);
        return (perOrder.Count, perOrder.DefaultIfEmpty(0).Max());
    }

    [Fact]
    public async Task O10_stock_allocated_before_release_stays_awaiting_stock()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var orderId = await LaunchOrderAsync(t, Guid.NewGuid(), paid: true);

        await AllocatedAsync(t, orderId);

        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.AwaitingStock, true, false, false), (order.Status, order.StockAllocated, order.LaunchReleased, order.HandedToFulfillment));
        Assert.Empty(await Outbox.PayloadsAsync<OrderReadyToFulfillV1>(t, OrderMessages.ReadyToFulfill));
        Assert.Equal(("pending_payment", "awaiting_stock", "system"), (await HistoryAsync(t, orderId)).Last());
    }

    [Fact]
    public async Task Released_then_allocated_moves_to_paid()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var launchId = Guid.NewGuid();
        var orderId = await LaunchOrderAsync(t, launchId, paid: true);

        await ReleaseAsync(t, launchId);
        await ReleaseAsync(t, launchId); // redelivered: nothing more is sent

        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.AwaitingStock, true, false), (order.Status, order.LaunchReleased, order.StockAllocated));
        var delayed = Assert.Single(await Outbox.PayloadsAsync<OrderPreorderDelayedV1>(t, OrderMessages.PreorderDelayed));
        Assert.Equal((orderId, order.Number, order.AccountId), (delayed.OrderId, delayed.OrderNumber, delayed.AccountId));
        Assert.Empty(await Outbox.PayloadsAsync<OrderReadyToFulfillV1>(t, OrderMessages.ReadyToFulfill));

        await AllocatedAsync(t, orderId);
        await AllocatedAsync(t, orderId); // redelivered

        order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.Paid, true, true, true), (order.Status, order.LaunchReleased, order.StockAllocated, order.HandedToFulfillment));
        Assert.Equal(("awaiting_stock", "paid", "system"), (await HistoryAsync(t, orderId)).Last());
        var ready = Assert.Single(await Outbox.PayloadsAsync<OrderReadyToFulfillV1>(t, OrderMessages.ReadyToFulfill));
        Assert.Equal((orderId, order.Number, order.TotalCents), (ready.OrderId, ready.Number, ready.TotalCents));
        Assert.Single(await Outbox.PayloadsAsync<OrderPreorderDelayedV1>(t, OrderMessages.PreorderDelayed));
    }

    [Fact]
    public async Task O11_release_with_5000_allocated_preorders()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var template = await LaunchOrderAsync(t, Guid.NewGuid(), paid: true);
        var launchId = Guid.NewGuid();
        var otherLaunch = Guid.NewGuid();
        Assert.Equal(5000, await CloneAsync(t, template, launchId, 5000, OrderStatuses.AwaitingStock, allocated: true));
        Assert.Equal(3, await CloneAsync(t, template, otherLaunch, 3, OrderStatuses.AwaitingStock, allocated: true));

        await ReleaseAsync(t, launchId);

        Assert.Equal(new Dictionary<string, int> { [OrderStatuses.Paid] = 5000 }, await CountsAsync(t, launchId));
        Assert.Equal(new Dictionary<string, int> { [OrderStatuses.AwaitingStock] = 3 }, await CountsAsync(t, otherLaunch));
        Assert.Equal((5000, 1), await PerOrderAsync(t, OrderMessages.ReadyToFulfill));
        Assert.Equal((0, 0), await PerOrderAsync(t, OrderMessages.PreorderDelayed));
        await using var db = t.NewDb();
        Assert.Equal(5000, await db.Orders.CountAsync(o => o.LaunchId == launchId && o.HandedToFulfillment && o.LaunchReleased, Ct));
        Assert.Equal(5000, await db.OrderStatusHistory.CountAsync(h => h.FromStatus == "awaiting_stock" && h.ToStatus == "paid" && h.ActorType == "system", Ct));
    }

    [Fact]
    public async Task Redelivered_release_after_a_partial_run_emits_once()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var template = await LaunchOrderAsync(t, Guid.NewGuid(), paid: true);
        var launchId = Guid.NewGuid();
        // The first delivery died after committing 1200 orders (their flags, status changes and events are in); the other 3800 were never reached.
        await CloneAsync(t, template, launchId, 1200, OrderStatuses.AwaitingStock, allocated: true);
        await CloneAsync(t, template, launchId, 2, OrderStatuses.AwaitingStock, allocated: false);
        await ReleaseAsync(t, launchId);
        Assert.Equal((1200, 1), await PerOrderAsync(t, OrderMessages.ReadyToFulfill));
        await CloneAsync(t, template, launchId, 3800, OrderStatuses.AwaitingStock, allocated: true);
        await CloneAsync(t, template, launchId, 2, OrderStatuses.AwaitingStock, allocated: false);

        await ReleaseAsync(t, launchId);

        Assert.Equal((5000, 1), await PerOrderAsync(t, OrderMessages.ReadyToFulfill));
        Assert.Equal((4, 1), await PerOrderAsync(t, OrderMessages.PreorderDelayed));
        Assert.Equal(new Dictionary<string, int> { [OrderStatuses.Paid] = 5000, [OrderStatuses.AwaitingStock] = 4 }, await CountsAsync(t, launchId));
    }

    [Fact]
    public async Task Release_survives_an_order_cancelled_while_its_batch_waits()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var template = await LaunchOrderAsync(t, Guid.NewGuid(), paid: true);
        var launchId = Guid.NewGuid();
        await CloneAsync(t, template, launchId, 1001, OrderStatuses.AwaitingStock, allocated: true);
        await using var locker = t.NewDb();
        await using var tx = await locker.Database.BeginTransactionAsync(Ct);
        // The first row of the first batch is locked, then cancelled before the batch gets it: the batch must skip it and still flag the rest (this Postgres plan refills the LIMIT; the loop does not rely on that).
        var first = await locker.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM orders WHERE launch_id = {launchId} ORDER BY id LIMIT 1 FOR UPDATE").SingleAsync(Ct);
        var release = ReleaseAsync(t, launchId);
        await using (var probe = t.NewDb())
            await Poll.UntilAsync(async () => await probe.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event_type = 'Lock'").SingleAsync(Ct) > 0, 10, "the first batch waiting on the locked order");
        await locker.Database.ExecuteSqlAsync($"UPDATE orders SET status = 'cancelled', cancel_reason = 'customer' WHERE id = {first}", Ct);
        await tx.CommitAsync(Ct);

        await release;

        Assert.Equal(new Dictionary<string, int> { [OrderStatuses.Paid] = 1000, [OrderStatuses.Cancelled] = 1 }, await CountsAsync(t, launchId));
        Assert.Equal((1000, 1), await PerOrderAsync(t, OrderMessages.ReadyToFulfill));
        await ReleaseAsync(t, launchId); // redelivered: an empty batch, nothing more
        Assert.Equal((1000, 1), await PerOrderAsync(t, OrderMessages.ReadyToFulfill));
    }

    [Fact]
    public async Task Release_flags_open_checkouts_of_the_launch()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var launchId = Guid.NewGuid();
        var pending = await LaunchOrderAsync(t, launchId, paid: false);
        var template = await LaunchOrderAsync(t, Guid.NewGuid(), paid: false);
        await CloneAsync(t, template, launchId, 1, OrderStatuses.Creating, allocated: false);
        Guid creating;
        await using (var db = t.NewDb()) creating = await db.Orders.AsNoTracking().Where(o => o.LaunchId == launchId && o.Status == OrderStatuses.Creating).Select(o => o.Id).SingleAsync(Ct);

        await AllocatedAsync(t, pending); // not awaiting_stock yet: nothing happens
        Assert.False((await OrderAsync(t, pending)).StockAllocated);

        await ReleaseAsync(t, launchId);

        var early = await OrderAsync(t, creating);
        Assert.Equal((true, OrderStatuses.Creating), (early.LaunchReleased, early.Status));
        var open = await OrderAsync(t, pending);
        Assert.Equal((true, OrderStatuses.PendingPayment), (open.LaunchReleased, open.Status));
        Assert.Empty(await Outbox.PayloadsAsync<OrderPreorderDelayedV1>(t, OrderMessages.PreorderDelayed));
        Assert.Empty(await Outbox.PayloadsAsync<OrderReadyToFulfillV1>(t, OrderMessages.ReadyToFulfill));

        await PaidAsync(t, pending);

        open = await OrderAsync(t, pending);
        Assert.Equal((OrderStatuses.AwaitingStock, true, false), (open.Status, open.LaunchReleased, open.StockAllocated));
        Assert.Empty(await Outbox.PayloadsAsync<OrderPreorderDelayedV1>(t, OrderMessages.PreorderDelayed)); // released before it was paid: nothing to announce

        await AllocatedAsync(t, pending);

        open = await OrderAsync(t, pending);
        Assert.Equal((OrderStatuses.Paid, true), (open.Status, open.HandedToFulfillment));
        Assert.Equal(pending, Assert.Single(await Outbox.PayloadsAsync<OrderReadyToFulfillV1>(t, OrderMessages.ReadyToFulfill)).OrderId);
    }

    [Fact]
    public async Task Other_stages_are_no_ops()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var launchId = Guid.NewGuid();
        var orderId = await LaunchOrderAsync(t, launchId, paid: true);

        foreach (var stage in new[] { "draft", "announced", "preorder_open" }) await ReleaseAsync(t, launchId, stage);
        await AllocatedAsync(t, Guid.NewGuid()); // unknown order

        var order = await OrderAsync(t, orderId);
        Assert.Equal((OrderStatuses.AwaitingStock, false, false), (order.Status, order.LaunchReleased, order.StockAllocated));
        Assert.Empty(await Outbox.PayloadsAsync<OrderPreorderDelayedV1>(t, OrderMessages.PreorderDelayed));
        Assert.Empty(await Outbox.PayloadsAsync<OrderReadyToFulfillV1>(t, OrderMessages.ReadyToFulfill));
    }
}
