using System.Net;
using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Worker;
using Bloom.Identity.Contracts;
using Bloom.Order.Contracts;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Fulfillment.Tests;

public class WorkerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestFulfillment Api, TestFulfillment Worker)> StartAsync()
    {
        var api = await TestFulfillment.StartAsync(containers);
        var worker = await TestFulfillment.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api.ConnectionString; });
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

    [Fact]
    public async Task Four_queues_are_bound()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var bindings = (await containers.RabbitCtlAsync("list_bindings", "-q"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();

        (string Queue, string Exchange, string Key)[] expected =
        [
            ("fulfillment.ready-to-fulfill", "order.events", OrderMessages.ReadyToFulfill),
            ("fulfillment.refund-completed", "payment.events", PaymentMessages.RefundCompleted),
            ("fulfillment.refund-failed", "payment.events", PaymentMessages.RefundFailed),
            ("fulfillment.account-deleted", "identity.events", IdentityMessages.AccountDeleted),
        ];
        foreach (var (queue, exchange, key) in expected)
            Assert.Contains(bindings, b => b.Length >= 5 && b[0] == exchange && b[2] == queue && b[4] == key);
    }

    [Fact]
    public async Task Ready_to_fulfill_through_the_broker_creates_one_task()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var (ready, sentinel) = (Seed.Ready(), Seed.Ready());
        var envelope = NewEnvelope(OrderMessages.ReadyToFulfill, "order", ready);

        await PublishAsync(channel, "order.events", envelope);
        await PublishAsync(channel, "order.events", envelope);
        await PublishAsync(channel, "order.events", NewEnvelope(OrderMessages.ReadyToFulfill, "order", sentinel)); // queued after the duplicates, so its task proves they were handled

        await Poll.UntilAsync(async () => await Seed.TaskAsync(api, sentinel.OrderId) is not null, 20, "sentinel task");
        await using var db = api.NewDb();
        Assert.Equal(1, await db.PackTasks.CountAsync(p => p.RefId == ready.OrderId, Ct));
        Assert.Equal(1, await db.OrderAccounts.CountAsync(a => a.OrderId == ready.OrderId, Ct));
    }

    [Fact]
    public async Task Commands_reach_inventory_and_payment_exchanges()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var inventoryQueue = await QueueOnAsync(channel, "inventory.commands", FulfillmentMessages.ReceiveCustomerReturn);
        var paymentQueue = await QueueOnAsync(channel, "payment.commands", PaymentMessages.RefundPayment);
        var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var ready = Seed.Ready();
        await Seed.ReadyAsync(api, ready);
        var customer = TestCaller.Customer(ready.AccountId);
        await Seed.DeliveredAsync(api, ready, $"WKA{id}", $"WKB{id}");
        var returnId = await Seed.InspectedReturnAsync(api, ready.OrderId, customer, (1, 1));

        using var decision = await api.SendAsync(HttpMethod.Post, $"/api/admin/fulfillment/returns/{returnId}/decision",
            new { units = new[] { new { unitNo = 1, decision = "full", partialCents = 0 } } }, TestCaller.Staff("support"), Guid.NewGuid());
        await decision.OkJsonAsync();

        var received = await NextAsync(channel, inventoryQueue, "receive-customer-return on inventory.commands");
        Assert.Equal(FulfillmentMessages.ReceiveCustomerReturn, received.MessageType);
        var refund = await NextAsync(channel, paymentQueue, "refund-payment on payment.commands");
        Assert.Equal(ready.OrderId, refund.GetPayload<RefundPaymentV1>().OrderId);
    }

    [Fact]
    public async Task Events_reach_fulfillment_events()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var shipped = await QueueOnAsync(channel, "fulfillment.events", FulfillmentMessages.ShipmentShipped);
        var audit = await QueueOnAsync(channel, "fulfillment.events", IdentityMessages.StaffActionRecorded);
        var ready = Seed.Ready();
        await Seed.ReadyAsync(api, ready);
        var (_, shipmentId) = await Seed.LabelReadyAsync(api, ready, $"EVA{Guid.NewGuid():N}"[..16], $"EVB{Guid.NewGuid():N}"[..16]);

        using var handover = await api.SendAsync(HttpMethod.Post, "/api/admin/fulfillment/handover", new { shipmentId }, TestCaller.Staff("warehouse"));
        await handover.OkJsonAsync();

        Assert.Equal(ready.OrderId, (await NextAsync(channel, shipped, "shipment-shipped on fulfillment.events")).GetPayload<ShipmentShippedV1>().OrderId);
        Assert.Equal(IdentityMessages.StaffActionRecorded, (await NextAsync(channel, audit, "staff-action-recorded on fulfillment.events")).MessageType);
    }

    [Fact]
    public async Task Monitor_sets_gauges_and_reports_stuck_sagas()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        using var metrics = new MetricCapture(worker.Services);
        var monitor = worker.Services.GetServices<IHostedService>().OfType<FulfillmentMonitor>().Single();
        var id = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var delivered = Seed.Ready();
        await Seed.ReadyAsync(api, delivered);
        var (_, shipmentId) = await Seed.DeliveredAsync(api, delivered, $"MNA{id}", $"MNB{id}");
        await Seed.ReturnAsync(api, delivered.OrderId, TestCaller.Customer(delivered.AccountId), (1, 1));
        await Seed.RunLabelWorkerAsync(api); // the return label comes from the API host's Shippo double; the worker host's own label worker has nothing due
        await Seed.ReadyAsync(api, Seed.Ready()); // a second, pending task
        var now = worker.Clock.GetUtcNow();
        var saga = new FulfillmentSaga { RefId = Guid.NewGuid(), Type = SagaTypes.ReturnRefund, PendingSteps = ["refund"], RefundId = Guid.NewGuid(), StartedAt = now.AddMinutes(-29) };
        await using (var db = api.NewDb())
        {
            db.DeliveryFailures.Add(new DeliveryFailure { ShipmentId = shipmentId, Kind = "lost", Status = FailureStatuses.PossiblyLost, PreviousStatus = "in_transit", CreatedAt = now, UpdatedAt = now });
            db.FulfillmentSagas.Add(saga);
            await db.SaveChangesAsync(Ct);
        }

        await monitor.RunOnceAsync(Ct);
        Assert.Equal(1, metrics.Latest("pack_tasks_by_status", ("status", "pending")));
        Assert.Equal(1, metrics.Latest("pack_tasks_by_status", ("status", "handed_over")));
        Assert.Equal(1, metrics.Latest("returns_by_status", ("status", "label_issued")));
        Assert.Equal(1, metrics.Latest("possibly_lost_open"));
        Assert.DoesNotContain(worker.Logs.Entries, e => e.StartsWith("[Error]"));

        await using (var db = api.NewDb())
            await db.FulfillmentSagas.ExecuteUpdateAsync(s => s.SetProperty(x => x.StartedAt, now.AddMinutes(-31)), Ct); // the clock stays put: moving it would also tick the hosted loops
        await monitor.RunOnceAsync(Ct);
        Assert.Single(worker.Logs.Entries, e => e.StartsWith("[Error]") && e.Contains("open for more than 30 minutes"));
    }

    [Fact]
    public async Task Dlq_admin_is_hosted()
    {
        var (api, worker) = await StartAsync(); // the worker declares the queues
        await using var _ = api;
        await using var __ = worker;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dlq/fulfillment/queues");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        request.Headers.Add("x-bloom-user-type", "staff");
        request.Headers.Add("x-bloom-role", "Admin"); // the value Bloom.Shared checks today
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());
        using var response = await api.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        foreach (var handler in new[] { "ready-to-fulfill", "refund-completed", "refund-failed", "account-deleted" })
            Assert.Contains($"fulfillment.{handler}.dlq", body);
    }

    [Fact]
    public async Task Worker_runs_label_worker_sweeps_and_monitor()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var hosted = worker.Services.GetServices<IHostedService>().ToList();
        Assert.Single(hosted.OfType<LabelWorker>());
        Assert.Single(hosted.OfType<Sweeps>());
        Assert.Single(hosted.OfType<FulfillmentMonitor>());
        var apiHosted = api.Services.GetServices<IHostedService>().ToList();
        Assert.Empty(apiHosted.OfType<LabelWorker>());
        Assert.Empty(apiHosted.OfType<Sweeps>());
        Assert.Empty(apiHosted.OfType<FulfillmentMonitor>());
    }
}
