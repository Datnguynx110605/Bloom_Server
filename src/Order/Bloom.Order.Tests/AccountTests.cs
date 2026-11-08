using Bloom.Identity.Contracts;
using Bloom.Order.Consumers;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Tests;

public class AccountTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<OrderRow> OrderAsync(TestOrder t, Guid account, string status, int openReturns = 0, DateTimeOffset? deliveredAt = null)
    {
        var now = t.Clock.GetUtcNow();
        var order = new OrderRow
        {
            Id = Guid.CreateVersion7(), Number = "BLM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), AccountId = account, Kind = OrderKinds.Regular, Status = status,
            ShippingOption = "standard", ShipTo = new ShipToV1("Ada Lovelace", ["1 Main St", "Apt 2"], "Reno", "NV", "89501", "+17755550100"), Currency = "USD",
            SubtotalCents = 2000, TaxCents = 165, ShippingCents = 500, TotalCents = 2665, OpenReturns = openReturns, DeliveredAt = deliveredAt, CreatedAt = now, UpdatedAt = now,
        };
        await using var db = t.NewDb();
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order;
    }

    private static async Task<bool> UnsettledAsync(TestOrder t, Guid account) =>
        (await t.Rpc.Internal.HasUnsettledOrdersAsync(new() { AccountId = account.ToString("D") }, cancellationToken: Ct)).HasUnsettledOrders;

    private static Task DeletedAsync(TestOrder t, Guid account) =>
        Consume.Async<AccountDeletedConsumer>(t, IdentityMessages.AccountDeleted, new AccountDeletedV1(account));

    [Fact]
    public async Task O20_account_deleted_anonymizes_and_deletes_the_cart()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var (account, other) = (Guid.NewGuid(), Guid.NewGuid());
        var first = await OrderAsync(t, account, OrderStatuses.Delivered, deliveredAt: t.Clock.GetUtcNow().AddDays(-30));
        var second = await OrderAsync(t, account, OrderStatuses.Cancelled);
        var kept = await OrderAsync(t, other, OrderStatuses.Paid);
        var device = Seed.Device(t.Upstreams);
        await Seed.CartAsync(t, account, (device, 1, null));
        await Seed.CartAsync(t, other, (device, 1, null));

        await DeletedAsync(t, account);
        await DeletedAsync(t, account); // a redelivery changes nothing

        await using var db = t.NewDb();
        foreach (var before in new[] { first, second })
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == before.Id, Ct);
            Assert.Equivalent(new ShipToV1("[deleted]", ["[deleted]", "[deleted]"], "Reno", "NV", "89501", "[deleted]"), order.ShipTo);
            Assert.Equal((before.AccountId, before.Number, before.Status, before.TotalCents, before.TaxCents, before.DeliveredAt, before.Version),
                (order.AccountId, order.Number, order.Status, order.TotalCents, order.TaxCents, order.DeliveredAt, order.Version));
        }
        Assert.Equivalent(kept.ShipTo, (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == kept.Id, Ct)).ShipTo);
        Assert.False(await db.Carts.AnyAsync(c => c.AccountId == account, Ct));
        Assert.False(await db.CartLines.AnyAsync(l => l.AccountId == account, Ct));
        Assert.True(await db.Carts.AnyAsync(c => c.AccountId == other, Ct));
        Assert.True(await db.CartLines.AnyAsync(l => l.AccountId == other, Ct));

        await DeletedAsync(t, Guid.NewGuid()); // an unknown account is a no-op
    }

    [Fact]
    public async Task O21_delivery_10_days_ago_is_unsettled()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var now = t.Clock.GetUtcNow();
        var (recent, old, edge) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await OrderAsync(t, recent, OrderStatuses.Delivered, deliveredAt: now.AddDays(-10));
        await OrderAsync(t, old, OrderStatuses.Delivered, deliveredAt: now.AddDays(-15));
        await OrderAsync(t, edge, OrderStatuses.Delivered, deliveredAt: now.AddDays(-14)); // strictly newer than 14 days

        Assert.True(await UnsettledAsync(t, recent));
        Assert.False(await UnsettledAsync(t, old));
        Assert.False(await UnsettledAsync(t, edge));
        Assert.False(await UnsettledAsync(t, Guid.NewGuid()));
    }

    [Fact]
    public async Task Unsettled_statuses_and_open_returns()
    {
        await using var t = await TestOrder.StartAsync(containers);

        foreach (var status in new[] { OrderStatuses.PendingPayment, OrderStatuses.Paid, OrderStatuses.AwaitingStock, OrderStatuses.Packing, OrderStatuses.Shipped })
        {
            var account = Guid.NewGuid();
            await OrderAsync(t, account, status);
            Assert.True(await UnsettledAsync(t, account), status);
        }
        foreach (var status in new[] { OrderStatuses.Cancelled, OrderStatuses.Returned, OrderStatuses.Creating })
        {
            var account = Guid.NewGuid();
            await OrderAsync(t, account, status);
            Assert.False(await UnsettledAsync(t, account), status);
        }
        var withReturn = Guid.NewGuid();
        await OrderAsync(t, withReturn, OrderStatuses.PartiallyReturned, openReturns: 1);
        Assert.True(await UnsettledAsync(t, withReturn));
    }
}
