using Bloom.Launch.Consumers;
using Bloom.Launch.Contracts;
using Bloom.Launch.Data;
using Bloom.Order.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Tests;

public class SettlementTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<TestLaunch> StartAsync() => TestLaunch.StartAsync(containers);

    private static string D(Guid id) => id.ToString("D");

    /// <summary>A launch (one product, limit 5, one SKU of <paramref name="allocation"/>) with a <c>held</c> hold of <paramref name="qty"/> for a new account.</summary>
    private static async Task<(SeededLaunch Launch, Guid Account, Guid Order)> HeldAsync(TestLaunch t, int allocation = 10, int qty = 2, SeededLaunch? on = null)
    {
        var launch = on ?? await Seed.LaunchAsync(t, products: new SeedProduct(5, allocation));
        var (account, order) = (Guid.NewGuid(), Guid.NewGuid());
        await Holds.CallAsync(t, account, Holds.Request(order, Holds.Token(t, launch.Id, account), (launch.Skus[0], qty)));
        return (launch, account, order);
    }

    private static Task Paid(TestLaunch t, Guid order, Guid? launch, Guid? messageId = null) =>
        Consume.Async<OrderPaidConsumer>(t, OrderMessages.Paid,
            new OrderPaidV1(order, "BL-1", Guid.NewGuid(), launch, DateTimeOffset.UtcNow, 100, [new(Guid.NewGuid(), 99)]), messageId);

    private static Task Release(TestLaunch t, Guid order) =>
        Consume.Async<ReleaseAllocationHoldConsumer>(t, LaunchMessages.ReleaseAllocationHold, new ReleaseAllocationHoldV1(order, "checkout_expired"));

    private static async Task<(int Held, int Sold)> SkuAsync(TestLaunch t, Guid sku)
    {
        await using var db = t.NewDb();
        var row = await db.LaunchSkus.AsNoTracking().SingleAsync(s => s.SkuId == sku, Ct);
        return (row.Held, row.Sold);
    }

    private static async Task<(int Held, int Sold)> LimitAsync(TestLaunch t, Guid launch, Guid account)
    {
        await using var db = t.NewDb();
        var row = await db.LimitUsage.AsNoTracking().SingleAsync(u => u.LaunchId == launch && u.AccountId == account, Ct);
        return (row.Held, row.Sold);
    }

    private static async Task<HoldRow> HoldAsync(TestLaunch t, Guid order)
    {
        await using var db = t.NewDb();
        return await db.Holds.AsNoTracking().SingleAsync(h => h.OrderId == order, Ct);
    }

    private static async Task<int> ReleasedEventsAsync(TestLaunch t, Guid order) =>
        (await Outbox.PayloadsAsync<HoldReleasedV1>(t, LaunchMessages.HoldReleased)).Count(p => p.OrderId == order);

    [Fact]
    public async Task Paid_moves_held_to_sold()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var (launch, account, order) = await HeldAsync(t);
        var before = await HoldAsync(t, order);
        var tags = new[] { ("launch", D(launch.Id)), ("sku", D(launch.Skus[0])) };
        Assert.Equal(8, metrics.Latest("allocation_available", tags));

        await Paid(t, order, launch.Id);

        Assert.Equal((0, 2), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((0, 2), await LimitAsync(t, launch.Id, account));
        var hold = await HoldAsync(t, order);
        Assert.Equal(HoldStatuses.Sold, hold.Status);
        Assert.Equal(before.Version + 1, hold.Version);
        Assert.True(hold.UpdatedAt > before.UpdatedAt);
        Assert.Equal(8, metrics.Latest("allocation_available", tags));
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task L10_paid_delivered_twice_sells_once()
    {
        await using var t = await StartAsync();
        var (launch, account, order) = await HeldAsync(t);
        var messageId = Guid.NewGuid();

        await Paid(t, order, launch.Id, messageId);
        await Paid(t, order, launch.Id, messageId); // the inbox drops this one in ConsumerHost; the status guard alone must hold too
        await Paid(t, order, launch.Id); // a new message for the same order

        Assert.Equal((0, 2), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((0, 2), await LimitAsync(t, launch.Id, account));
        Assert.Equal(1, (await HoldAsync(t, order)).Version);
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task Paid_without_launch_or_without_hold_is_a_no_op()
    {
        await using var t = await StartAsync();
        var (launch, account, order) = await HeldAsync(t);

        await Paid(t, order, null); // a regular order
        await Paid(t, Guid.NewGuid(), launch.Id); // no hold for this order

        Assert.Equal((2, 0), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((2, 0), await LimitAsync(t, launch.Id, account));
        Assert.Equal(HoldStatuses.Held, (await HoldAsync(t, order)).Status);
    }

    [Fact]
    public async Task L09_release_of_a_held_hold_returns_units_and_next_tick_admits()
    {
        await using var t = await StartAsync();
        var seeded = await Seed.LaunchAsync(t, products: new SeedProduct(5, 1));
        var waiting = Guid.NewGuid();
        await t.Customer.JoinWaitingRoomAsync(new() { LaunchId = D(seeded.Id) }, Api.Customer(waiting), cancellationToken: Ct).ResponseAsync; // queues before the unit is taken
        var (launch, account, order) = await HeldAsync(t, qty: 1, on: seeded);

        Assert.Equal("none", (await t.TickAsync(launch.Id)).Result);

        await Release(t, order);

        Assert.Equal((0, 0), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((0, 0), await LimitAsync(t, launch.Id, account));
        Assert.Equal(HoldStatuses.Released, (await HoldAsync(t, order)).Status);
        Assert.Equal(1, await ReleasedEventsAsync(t, order));
        var tick = await t.TickAsync(launch.Id);
        Assert.Equal("admitted", tick.Result);
        Assert.Equal([waiting], tick.Admitted);
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task Release_after_paid_returns_sold_units()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var (launch, account, order) = await HeldAsync(t);
        await Paid(t, order, launch.Id);

        await Release(t, order);

        Assert.Equal((0, 0), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((0, 0), await LimitAsync(t, launch.Id, account));
        Assert.Equal(HoldStatuses.Released, (await HoldAsync(t, order)).Status);
        Assert.Equal(1, await ReleasedEventsAsync(t, order));
        Assert.Equal(10, metrics.Latest("allocation_available", ("launch", D(launch.Id)), ("sku", D(launch.Skus[0]))));
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task L11_release_before_paid_then_paid_is_a_no_op()
    {
        await using var t = await StartAsync();
        var (launch, account, order) = await HeldAsync(t);
        await Release(t, order);
        var released = await HoldAsync(t, order);

        await Paid(t, order, launch.Id);

        var hold = await HoldAsync(t, order);
        Assert.Equal(HoldStatuses.Released, hold.Status);
        Assert.Equal(released.Version, hold.Version);
        Assert.Equal((0, 0), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((0, 0), await LimitAsync(t, launch.Id, account));
    }

    [Fact]
    public async Task Release_is_always_answered()
    {
        await using var t = await StartAsync();
        var (_, _, order) = await HeldAsync(t);
        var unknown = Guid.NewGuid();

        await Release(t, unknown);
        await Release(t, order);
        await Release(t, order); // already released

        Assert.Equal(1, await ReleasedEventsAsync(t, unknown));
        Assert.Equal(2, await ReleasedEventsAsync(t, order));
        Assert.Equal(HoldStatuses.Released, (await HoldAsync(t, order)).Status);
        Assert.Equal(1, (await HoldAsync(t, order)).Version);
    }

    [Fact]
    public async Task Released_customer_must_queue_again_at_the_back()
    {
        await using var t = await StartAsync();
        var (launch, account, order) = await HeldAsync(t);
        foreach (var other in new[] { Guid.NewGuid(), Guid.NewGuid() })
        {
            t.Clock.Advance(TimeSpan.FromSeconds(1)); // the score is the join time, so a tie would order by account id
            await t.Customer.JoinWaitingRoomAsync(new() { LaunchId = D(launch.Id) }, Api.Customer(other), cancellationToken: Ct).ResponseAsync;
        }

        await Release(t, order);
        t.Clock.Advance(TimeSpan.FromSeconds(1));
        var status = await t.Customer.JoinWaitingRoomAsync(new() { LaunchId = D(launch.Id) }, Api.Customer(account), cancellationToken: Ct).ResponseAsync;

        Assert.Equal("queued", status.State);
        Assert.Equal(3, status.Position);
    }

    [Fact]
    public async Task Cancelled_after_close_keeps_the_room_closed()
    {
        await using var t = await StartAsync();
        var (launch, account, order) = await HeldAsync(t, allocation: 1, qty: 1);
        await Paid(t, order, launch.Id);
        await t.TickAsync(launch.Id); // sold out with no held hold: the room closes for good
        await using var db = t.NewDb();
        var closedAt = (await db.Launches.AsNoTracking().SingleAsync(l => l.Id == launch.Id, Ct)).WaitingRoomClosedAt;
        Assert.NotNull(closedAt);

        await Release(t, order);

        Assert.Equal((0, 0), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((0, 0), await LimitAsync(t, launch.Id, account));
        await using var after = t.NewDb();
        Assert.Equal(closedAt, (await after.Launches.AsNoTracking().SingleAsync(l => l.Id == launch.Id, Ct)).WaitingRoomClosedAt);
        var tick = await t.TickAsync(launch.Id);
        Assert.Equal("none", tick.Result);
        Assert.Empty(tick.Admitted);
    }

    [Fact]
    public async Task Paid_and_release_at_once_end_released()
    {
        await using var t = await StartAsync();
        var launch = await Seed.LaunchAsync(t, products: new SeedProduct(5, 100));
        for (var round = 0; round < 20; round++)
        {
            var (_, account, order) = await HeldAsync(t, qty: 2, on: launch);
            var paid = Task.Run(() => Paid(t, order, launch.Id), Ct);
            var release = Task.Run(() => Release(t, order), Ct);
            await Task.WhenAll(round % 2 == 0 ? [paid, release] : [release, paid]);

            Assert.Equal(HoldStatuses.Released, (await HoldAsync(t, order)).Status);
            Assert.Equal((0, 0), await LimitAsync(t, launch.Id, account));
            Assert.Equal((0, 0), await SkuAsync(t, launch.Skus[0]));
            Assert.Equal(1, await ReleasedEventsAsync(t, order));
        }
        await Holds.InvariantAsync(t, launch.Id);
    }

    [Fact]
    public async Task Stuck_holds_are_reported()
    {
        await using var t = await StartAsync();
        using var metrics = new MetricCapture(t.Services);
        var (stuck, _, stuckOrder) = await HeldAsync(t);
        var (fresh, _, freshOrder) = await HeldAsync(t);
        await t.SqlAsync($"UPDATE holds SET created_at = now() - interval '31 minutes' WHERE order_id = {stuckOrder}");
        await t.SqlAsync($"UPDATE holds SET created_at = now() - interval '29 minutes' WHERE order_id = {freshOrder}");

        await t.RunMonitorAsync();

        Assert.Equal(1, metrics.Latest("stuck_holds", ("launch", D(stuck.Id))));
        Assert.Equal(1, t.ErrorLogs);
        Assert.Contains(t.Logs.Entries, e => e.StartsWith("[Error]", StringComparison.Ordinal) && e.Contains(D(stuck.Id)));
        Assert.DoesNotContain(t.Logs.Entries, e => e.Contains(D(fresh.Id)) && e.StartsWith("[Error]", StringComparison.Ordinal));

        await Release(t, stuckOrder);
        await t.RunMonitorAsync();

        Assert.Equal(0, metrics.Latest("stuck_holds", ("launch", D(stuck.Id))));
        Assert.Equal(1, t.ErrorLogs);
    }

    [Fact]
    public async Task Bad_payload_dead_letters_at_once()
    {
        await using var t = await StartAsync();
        var (launch, account, order) = await HeldAsync(t);

        await Assert.ThrowsAsync<PermanentFailureException>(() =>
            Consume.Async<ReleaseAllocationHoldConsumer>(t, LaunchMessages.ReleaseAllocationHold, new { reason = "checkout_expired" }));

        Assert.Equal(HoldStatuses.Held, (await HoldAsync(t, order)).Status);
        Assert.Equal((2, 0), await SkuAsync(t, launch.Skus[0]));
        Assert.Equal((2, 0), await LimitAsync(t, launch.Id, account));
        Assert.Empty(await Outbox.PayloadsAsync<HoldReleasedV1>(t, LaunchMessages.HoldReleased));
    }
}
