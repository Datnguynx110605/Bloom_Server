using System.Net;
using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Identity.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Inventory.Data;
using Bloom.Inventory.Worker;
using Bloom.Order.Contracts;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using RabbitMQ.Client;

namespace Bloom.Inventory.Tests;

public class WorkerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestInventory Api, TestInventory Worker)> StartAsync()
    {
        var api = await TestInventory.StartAsync(containers);
        var worker = await TestInventory.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api.ConnectionString; });
        return (api, worker);
    }

    private async Task<(IConnection Connection, IChannel Channel)> RabbitAsync()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync(Ct);
        return (connection, await connection.CreateChannelAsync(cancellationToken: Ct));
    }

    private static Envelope NewEnvelope(string messageType, string kind, string producer, object payload) =>
        new(Guid.CreateVersion7(), messageType, kind, producer, DateTime.UtcNow, Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web));

    private static Task PublishAsync(IChannel channel, string exchange, Envelope e) =>
        channel.BasicPublishAsync(exchange, e.MessageType, false,
            new BasicProperties { MessageId = e.MessageId.ToString(), Type = e.MessageType, DeliveryMode = DeliveryModes.Persistent }, e.ToUtf8Bytes()).AsTask();

    private static Envelope PaidEnvelope(Guid orderId, Guid sku, int quantity) =>
        NewEnvelope(OrderMessages.Paid, MessageKinds.Event, "order",
            new OrderPaidV1(orderId, "BL-1", Guid.CreateVersion7(), null, DateTimeOffset.UtcNow, 1000, [new OrderPaidLineV1(sku, quantity)]));

    private static async Task<string> QueueOnAsync(IChannel channel, string exchange, string key)
    {
        var queue = $"test-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(queue, exchange, key, cancellationToken: Ct);
        return queue;
    }

    [Fact]
    public async Task Six_queues_are_bound()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var bindings = (await containers.RabbitCtlAsync("list_bindings", "-q"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();

        (string Queue, string Exchange, string Key)[] expected =
        [
            ("inventory.order-paid", "order.events", OrderMessages.Paid),
            ("inventory.shipment-shipped", "fulfillment.events", FulfillmentMessages.ShipmentShipped),
            ("inventory.release-reservation", "inventory.commands", InventoryMessages.ReleaseReservation),
            ("inventory.receive-customer-return", "inventory.commands", FulfillmentMessages.ReceiveCustomerReturn),
            ("inventory.receive-returned-to-sender", "inventory.commands", FulfillmentMessages.ReceiveReturnedToSender),
            ("inventory.mark-lost-in-transit", "inventory.commands", FulfillmentMessages.MarkLostInTransit),
        ];
        foreach (var (queue, exchange, key) in expected)
            Assert.Contains(bindings, b => b.Length >= 5 && b[0] == exchange && b[2] == queue && b[4] == key);
    }

    [Fact]
    public async Task I17_published_paid_twice_is_applied_once()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var sku = Seed.Sku(api);
        await Seed.ReceiveAsync(api, sku, 5);
        var (order, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Seed.ReserveAsync(api, order, (sku, 2));
        await Seed.ReserveAsync(api, other, (sku, 1));
        var paid = PaidEnvelope(order, sku, 2);
        var sentinel = PaidEnvelope(other, sku, 1); // queued after the duplicates, so its effect proves they were handled

        await PublishAsync(channel, "order.events", paid);
        await PublishAsync(channel, "order.events", paid);
        await PublishAsync(channel, "order.events", sentinel);

        await using var db = api.NewDb();
        await Poll.UntilAsync(async () => (await db.StockItems.AsNoTracking().SingleAsync(i => i.SkuId == sku, Ct)).Committed == 3, 20, "both orders committed");
        var item = await db.StockItems.AsNoTracking().SingleAsync(i => i.SkuId == sku, Ct);
        Assert.Equal((2, 0, 3), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(1, await db.Set<InboxMessage>().CountAsync(m => m.MessageId == paid.MessageId, Ct));
    }

    [Fact]
    public async Task I12_shipment_missing_serials_lands_in_the_dlq()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var sku = Seed.Sku(api, serialized: true);
        await Seed.ReceiveAsync(api, sku, 2, ["ABC123", "ABC124"]);
        var order = Guid.CreateVersion7();
        await Seed.ReserveAsync(api, order, (sku, 2));
        await Consume.Async<Consumers.OrderPaidConsumer>(api, OrderMessages.Paid,
            new OrderPaidV1(order, "BL-1", Guid.CreateVersion7(), null, DateTimeOffset.UtcNow, 1000, [new OrderPaidLineV1(sku, 2)]));
        await api.Packing.AssignSerialAsync(new() { RefId = order.ToString(), SkuId = sku.ToString(), Serial = "ABC123" }, cancellationToken: Ct); // one of two
        var e = NewEnvelope(FulfillmentMessages.ShipmentShipped, MessageKinds.Event, "fulfillment",
            new ShipmentShippedV1(order, order, [new ShippedLineV1(sku, 2, [])], "TRK1"));

        await PublishAsync(channel, "fulfillment.events", e);

        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync("inventory.shipment-shipped.dlq", true, Ct)) is not null, 20, "message in inventory.shipment-shipped.dlq");
        Assert.Equal(e.MessageId, Envelope.Parse(got!.Body.Span).MessageId);
        await using var db = api.NewDb();
        var item = await db.StockItems.AsNoTracking().SingleAsync(i => i.SkuId == sku, Ct);
        Assert.Equal((0, 0, 2), (item.OnHand, item.Reserved, item.Committed));
        Assert.Equal(AllocationStatuses.Committed, (await db.StockAllocations.AsNoTracking().SingleAsync(a => a.RefId == order, Ct)).Status);
        Assert.Equal(0, await db.Set<InboxMessage>().CountAsync(m => m.MessageId == e.MessageId, Ct));
    }

    [Fact]
    public async Task Release_command_reaches_order()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var queue = await QueueOnAsync(channel, "inventory.events", InventoryMessages.ReservationReleased);
        var sku = Seed.Sku(api);
        await Seed.ReceiveAsync(api, sku, 3);
        var order = Guid.CreateVersion7();
        await Seed.ReserveAsync(api, order, (sku, 2));

        await PublishAsync(channel, "inventory.commands", NewEnvelope(InventoryMessages.ReleaseReservation, MessageKinds.Command, "order", new ReleaseReservationV1(order)));

        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync(queue, true, Ct)) is not null, 20, "reservation-released on inventory.events");
        Assert.Equal(order, Envelope.Parse(got!.Body.Span).GetPayload<ReservationReleasedV1>().OrderId);
        var item = (await Seed.ItemAsync(api, sku))!;
        Assert.Equal((3, 0), (item.OnHand, item.Reserved));
    }

    [Fact]
    public async Task Audit_events_reach_inventory_events()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var queue = await QueueOnAsync(channel, "inventory.events", IdentityMessages.StaffActionRecorded);

        await Seed.ReceiveAsync(api, Seed.Sku(api), 1);

        var sent = Assert.Single(await Outbox.AuditAsync(api));
        Assert.Equal(("inventory.goods-received", "inventory"), (sent.Action, sent.Service));
        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync(queue, true, Ct)) is not null, 20, "staff-action-recorded on inventory.events");
        var envelope = Envelope.Parse(got!.Body.Span);
        Assert.Equal("inventory.goods-received", envelope.GetPayload<StaffActionRecorded>().Action);
        await using var db = api.NewDb();
        await Poll.UntilAsync(async () => (await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == envelope.MessageId, Ct)).Status == "sent", 20, "outbox row sent");
    }

    [Fact]
    public async Task Stock_monitor_reports_stuck_reservations_and_levels()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        using var metrics = new MetricCapture(worker.Services);
        var monitor = worker.Services.GetServices<IHostedService>().OfType<StockMonitor>().Single();
        api.Clock.SetUtcNow(worker.Clock.GetUtcNow()); // one business clock for both hosts
        var sku = Seed.Sku(api);
        await Seed.ReceiveAsync(api, sku, 5);
        var order = Guid.CreateVersion7();
        await Seed.ReserveAsync(api, order, (sku, 2));
        await Seed.BackorderAsync(api, sku, 10, api.Clock.GetUtcNow());

        worker.Clock.Advance(TimeSpan.FromMinutes(29));
        await monitor.RunOnceAsync(Ct);
        Assert.Equal(0, metrics.Latest("stuck_reservations"));

        worker.Clock.Advance(TimeSpan.FromMinutes(2));
        await monitor.RunOnceAsync(Ct);
        Assert.Equal(1, metrics.Latest("stuck_reservations"));
        Assert.Equal(3, metrics.Latest("stock_available", ("sku", sku.ToString())));
        Assert.Equal(1, metrics.Latest("backorders_waiting", ("sku", sku.ToString())));
        await using var db = api.NewDb();
        Assert.Equal(AllocationStatuses.Reserved, (await db.StockAllocations.AsNoTracking().SingleAsync(a => a.RefId == order, Ct)).Status); // reported, never released (IR-05)
    }

    [Fact]
    public async Task Dlq_admin_is_hosted()
    {
        var (api, worker) = await StartAsync(); // the worker declares the queues
        await using var _ = api;
        await using var __ = worker;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dlq/inventory/queues");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        request.Headers.Add("x-bloom-user-type", "staff");
        request.Headers.Add("x-bloom-role", "admin");
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());
        using var response = await api.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        foreach (var handler in new[] { "order-paid", "shipment-shipped", "release-reservation", "receive-customer-return", "receive-returned-to-sender", "mark-lost-in-transit" })
            Assert.Contains($"inventory.{handler}.dlq", body);
    }

    [Fact]
    public async Task Worker_runs_the_stock_monitor()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        Assert.Single(worker.Services.GetServices<IHostedService>().OfType<StockMonitor>());
        Assert.Empty(api.Services.GetServices<IHostedService>().OfType<StockMonitor>());
    }

    [Fact]
    public async Task D16_purger_deletes_only_older_than_30_days()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (old, recent) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await using (var db = api.NewDb())
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO released_orders (order_id, released_at) VALUES ({old}, now() - interval '31 days'), ({recent}, now() - interval '29 days')
                """, Ct);

        await worker.Services.GetServices<IHostedService>().OfType<TombstonePurger>().Single().RunOnceAsync(Ct);

        Assert.Equal(0, await Seed.TombstonesAsync(api, old));
        Assert.Equal(1, await Seed.TombstonesAsync(api, recent));
        Assert.Empty(api.Services.GetServices<IHostedService>().OfType<TombstonePurger>());
    }
}
