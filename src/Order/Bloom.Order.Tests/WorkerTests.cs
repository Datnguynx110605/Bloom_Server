using System.Net;
using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Identity.Contracts;
using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Api;
using Bloom.Order.Consumers;
using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Worker;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Order.Tests;

public class WorkerTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private async Task<(TestOrder Api, TestOrder Worker)> StartAsync()
    {
        var api = await TestOrder.StartAsync(containers);
        var worker = await TestOrder.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api.ConnectionString; });
        return (api, worker);
    }

    private async Task<(IConnection Connection, IChannel Channel)> RabbitAsync()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync(Ct);
        return (connection, await connection.CreateChannelAsync(cancellationToken: Ct));
    }

    private static Envelope NewEnvelope(string messageType, string producer, object payload) =>
        new(Guid.CreateVersion7(), messageType, MessageKinds.Event, producer, DateTime.UtcNow, Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web));

    private static Task PublishAsync(IChannel channel, string exchange, Envelope e) =>
        channel.BasicPublishAsync(exchange, e.MessageType, false,
            new BasicProperties { MessageId = e.MessageId.ToString(), Type = e.MessageType, DeliveryMode = DeliveryModes.Persistent }, e.ToUtf8Bytes()).AsTask();

    private static async Task<string> QueueOnAsync(IChannel channel, string exchange, string key)
    {
        var queue = $"test-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(queue, exchange, key, cancellationToken: Ct);
        return queue;
    }

    private static async Task<Envelope> NextAsync(IChannel channel, string queue, string what)
    {
        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync(queue, true, Ct)) is not null, 20, what);
        return Envelope.Parse(got!.Body.Span);
    }

    /// <summary>A <c>pending_payment</c> regular order of one device (2 x 1000), or a launch order.</summary>
    private static async Task<(Guid Account, Guid OrderId)> PendingAsync(TestOrder t, bool launch = false)
    {
        var account = Guid.NewGuid();
        var launchId = Guid.NewGuid();
        var device = launch ? Seed.LaunchSku(t.Upstreams, launchId, priceCents: 1000) : Seed.Device(t.Upstreams, 1000);
        if (!launch) await Seed.CartAsync(t, account, (device, 2, null));
        var response = await Seed.CheckoutAsync(t, account, configure: r =>
        {
            if (launch) r.Launch = new LaunchSource { LaunchId = launchId.ToString("D"), PassToken = "pass-1", Lines = { new CheckoutLine { Kind = "product", SkuId = device.SkuId, Quantity = 2 } } };
        });
        return (account, Guid.Parse(response.OrderId));
    }

    private static Task PayAsync(TestOrder t, Guid orderId) =>
        Consume.Async<PaymentSucceededConsumer>(t, PaymentMessages.Succeeded, new PaymentSucceededV1(orderId, FakeUpstreams.PaymentIdOf(orderId.ToString("D")), 0, t.Clock.GetUtcNow()));

    private static async Task CancelAsync(TestOrder t, Guid account, Guid orderId)
    {
        await using var db = t.NewDb();
        var version = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Version).SingleAsync(Ct);
        await t.Rpc.Customer.CancelOrderAsync(new CancelOrderRequest { OrderId = orderId.ToString("D"), Version = version }, TestCaller.Customer(account).Call());
    }

    [Fact]
    public async Task Every_queue_is_bound()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var bindings = (await containers.RabbitCtlAsync("list_bindings", "-q"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();

        (string Queue, string Exchange, string Key)[] expected =
        [
            ("payment-succeeded", "payment.events", PaymentMessages.Succeeded),
            ("payment-failed", "payment.events", PaymentMessages.Failed),
            ("payment-refund-completed", "payment.events", PaymentMessages.RefundCompleted),
            ("payment-refund-failed", "payment.events", PaymentMessages.RefundFailed),
            ("inventory-stock-allocated", "inventory.events", InventoryMessages.StockAllocated),
            ("inventory-reservation-released", "inventory.events", InventoryMessages.ReservationReleased),
            ("launch-hold-released", "launch.events", LaunchMessages.HoldReleased),
            ("launch-stage-changed", "launch.events", LaunchMessages.StageChanged),
            ("fulfillment-packing-started", "fulfillment.events", FulfillmentMessages.PackingStarted),
            ("fulfillment-shipment-shipped", "fulfillment.events", FulfillmentMessages.ShipmentShipped),
            ("fulfillment-shipment-delivered", "fulfillment.events", FulfillmentMessages.ShipmentDelivered),
            ("fulfillment-delivery-failed", "fulfillment.events", FulfillmentMessages.DeliveryFailed),
            ("fulfillment-return-requested", "fulfillment.events", FulfillmentMessages.ReturnRequested),
            ("fulfillment-return-closed", "fulfillment.events", FulfillmentMessages.ReturnClosed),
            ("fulfillment-return-decided", "fulfillment.events", FulfillmentMessages.ReturnDecided),
            ("identity-account-deleted", "identity.events", IdentityMessages.AccountDeleted),
        ];
        Assert.Equal(16, expected.Length);
        foreach (var (queue, exchange, key) in expected)
            Assert.Contains(bindings, b => b.Length >= 5 && b[0] == exchange && b[2] == $"order.{queue}" && b[4] == key);
    }

    [Fact]
    public async Task Payment_succeeded_through_the_broker_pays_once()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var ready = await QueueOnAsync(channel, "order.events", OrderMessages.ReadyToFulfill);
        var (_, orderId) = await PendingAsync(api);
        var (_, sentinelId) = await PendingAsync(api);
        var envelope = NewEnvelope(PaymentMessages.Succeeded, "payment", new PaymentSucceededV1(orderId, Guid.NewGuid(), 0, DateTimeOffset.UtcNow));

        await PublishAsync(channel, "payment.events", envelope);
        await PublishAsync(channel, "payment.events", envelope);
        await PublishAsync(channel, "payment.events", NewEnvelope(PaymentMessages.Succeeded, "payment", new PaymentSucceededV1(sentinelId, Guid.NewGuid(), 0, DateTimeOffset.UtcNow))); // queued after the duplicates

        var seen = new List<Guid>();
        while (!seen.Contains(sentinelId)) seen.Add((await NextAsync(channel, ready, "ready-to-fulfill on order.events")).GetPayload<OrderReadyToFulfillV1>().OrderId);
        Assert.Equal(1, seen.Count(id => id == orderId));
        await using var db = api.NewDb();
        Assert.Equal(OrderStatuses.Paid, await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync(Ct));
    }

    [Fact]
    public async Task Commands_reach_inventory_launch_and_payment()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var releaseReservation = await QueueOnAsync(channel, "inventory.commands", InventoryMessages.ReleaseReservation);
        var releaseHold = await QueueOnAsync(channel, "launch.commands", LaunchMessages.ReleaseAllocationHold);
        var refund = await QueueOnAsync(channel, "payment.commands", PaymentMessages.RefundPayment);
        var cancelPayment = await QueueOnAsync(channel, "payment.commands", PaymentMessages.CancelPayment);

        var (account, launchOrder) = await PendingAsync(api, launch: true);
        await PayAsync(api, launchOrder);
        await CancelAsync(api, account, launchOrder); // launch cancellation: release-allocation-hold, release-reservation and refund
        var (_, expiring) = await PendingAsync(api);
        api.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.True(await api.Sweeps().RunExpiryAsync(Ct) >= 1); // expiry: release-reservation and cancel-payment

        Assert.Equal(launchOrder, (await NextAsync(channel, releaseHold, "release-allocation-hold on launch.commands")).GetPayload<ReleaseAllocationHoldV1>().OrderId);
        Assert.Equal(launchOrder, (await NextAsync(channel, refund, "refund-payment on payment.commands")).GetPayload<RefundPaymentV1>().OrderId);
        Assert.Equal(expiring, (await NextAsync(channel, cancelPayment, "cancel-payment on payment.commands")).GetPayload<CancelPaymentV1>().OrderId);
        var released = new[] { (await NextAsync(channel, releaseReservation, "release-reservation on inventory.commands")).GetPayload<ReleaseReservationV1>().OrderId,
            (await NextAsync(channel, releaseReservation, "second release-reservation on inventory.commands")).GetPayload<ReleaseReservationV1>().OrderId };
        Assert.Equal(new[] { launchOrder, expiring }.Order(), released.Order());
    }

    [Fact]
    public async Task Events_reach_order_events()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var paid = await QueueOnAsync(channel, "order.events", OrderMessages.Paid);
        var cancelled = await QueueOnAsync(channel, "order.events", OrderMessages.Cancelled);
        var audit = await QueueOnAsync(channel, "order.events", IdentityMessages.StaffActionRecorded);
        var (account, orderId) = await PendingAsync(api);
        await PayAsync(api, orderId);
        await using (var db = api.NewDb())
        {
            var version = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Version).SingleAsync(Ct);
            await api.Rpc.Staff.SupportCancelOrderAsync(new SupportCancelOrderRequest { OrderId = orderId.ToString("D"), Reason = "Fraud suspected", Version = version }, TestCaller.Staff("support").Call());
        }

        Assert.Equal(orderId, (await NextAsync(channel, paid, "order.paid.v1 on order.events")).GetPayload<OrderPaidV1>().OrderId);
        Assert.Equal(orderId, (await NextAsync(channel, cancelled, "order.cancelled.v1 on order.events")).GetPayload<OrderCancelledV1>().OrderId);
        Assert.Equal(IdentityMessages.StaffActionRecorded, (await NextAsync(channel, audit, "staff-action-recorded on order.events")).MessageType);
    }

    [Fact]
    public async Task Monitor_sets_gauges_and_reports_stuck_sagas()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        using var metrics = new MetricCapture(worker.Services);
        var monitor = worker.Services.GetServices<IHostedService>().OfType<OrderMonitor>().Single();
        var (_, paid) = await PendingAsync(api);
        await PayAsync(api, paid);
        var (_, pending) = await PendingAsync(api);
        var (_, expired) = await PendingAsync(api);
        var now = worker.Clock.GetUtcNow();
        await using (var db = api.NewDb())
        {
            db.Set<OrderSaga>().Add(new OrderSaga { OrderId = expired, Type = SagaTypes.Expiry, PendingSteps = [SagaSteps.InventoryRelease], StartedAt = now.AddMinutes(-29) });
            await db.SaveChangesAsync(Ct);
        }

        await monitor.RunOnceAsync(Ct);
        Assert.Equal(1, metrics.Latest("orders_by_status", ("status", "paid")));
        Assert.Equal(2, metrics.Latest("orders_by_status", ("status", "pending_payment")));
        Assert.Equal(0, metrics.Latest("orders_by_status", ("status", "shipped"))); // a status without rows is reported as 0
        Assert.Equal(1, metrics.Latest("sagas_running", ("type", "expiry")));
        Assert.Equal(0, metrics.Latest("sagas_running", ("type", "cancellation")));
        Assert.Equal(0, metrics.Latest("sagas_stuck", ("type", "expiry")));
        Assert.DoesNotContain(worker.Logs.Entries, e => e.StartsWith("[Error]"));

        await using (var db = api.NewDb())
            await db.Set<OrderSaga>().ExecuteUpdateAsync(s => s.SetProperty(x => x.StartedAt, now.AddMinutes(-31)), Ct); // the clock stays put: moving it would also tick the hosted loops
        await monitor.RunOnceAsync(Ct);
        Assert.Equal(1, metrics.Latest("sagas_stuck", ("type", "expiry")));
        Assert.Equal(1, metrics.Latest("sagas_running", ("type", "expiry")));
        Assert.Single(worker.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains("open for more than 30 minutes") && e.Contains(expired.ToString()) && e.Contains('1'));
    }

    [Fact]
    public async Task Dlq_admin_is_hosted()
    {
        var (api, worker) = await StartAsync(); // the worker declares the queues
        await using var _ = api;
        await using var __ = worker;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dlq/order/queues");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        request.Headers.Add("x-bloom-user-type", "staff");
        request.Headers.Add("x-bloom-role", "admin");
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());
        using var response = await api.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        string[] handlers =
        [
            "payment-succeeded", "payment-failed", "payment-refund-completed", "payment-refund-failed", "inventory-stock-allocated", "inventory-reservation-released",
            "launch-hold-released", "launch-stage-changed", "fulfillment-packing-started", "fulfillment-shipment-shipped", "fulfillment-shipment-delivered",
            "fulfillment-delivery-failed", "fulfillment-return-requested", "fulfillment-return-closed", "fulfillment-return-decided", "identity-account-deleted",
        ];
        foreach (var handler in handlers) Assert.Contains($"order.{handler}.dlq", body);
        Assert.Equal(16, handlers.Length);
    }

    [Fact]
    public async Task Worker_runs_sweeps_and_monitor()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var hosted = worker.Services.GetServices<IHostedService>().ToList();
        Assert.Single(hosted.OfType<Sweeps>());
        Assert.Single(hosted.OfType<OrderMonitor>());
        var apiHosted = api.Services.GetServices<IHostedService>().ToList();
        Assert.Empty(apiHosted.OfType<Sweeps>());
        Assert.Empty(apiHosted.OfType<OrderMonitor>());
    }
}
