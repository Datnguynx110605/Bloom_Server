using System.Net;
using System.Text.Json;
using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Worker;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Payment.Tests;

/// <summary>Worker mode through the real broker (spec §4 "Testing across services"; plan R6): an API host and a worker host on one database and one Stripe double.</summary>
public class WorkerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestPayment Api, TestPayment Worker)> StartAsync()
    {
        var api = await TestPayment.StartAsync(containers);
        var worker = await TestPayment.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api.ConnectionString; o.Stripe = api.Stripe; });
        return (api, worker);
    }

    private async Task<(IConnection Connection, IChannel Channel)> RabbitAsync()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync(Ct);
        return (connection, await connection.CreateChannelAsync(cancellationToken: Ct));
    }

    private static Envelope NewCommand(string messageType, object payload) =>
        new(Guid.CreateVersion7(), messageType, MessageKinds.Command, "order", DateTime.UtcNow, Guid.NewGuid(), JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web));

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

    private static async Task<RefundRow?> RefundAsync(TestPayment t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.Refunds.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, Ct);
    }

    /// <summary>The worker's clock ticks the hosted executor (<c>PeriodicTimer(5 s)</c> on the test clock).</summary>
    private static void TickExecutor(TestPayment worker) => worker.Clock.Advance(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Every_queue_is_bound()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var bindings = (await containers.RabbitCtlAsync("list_bindings", "-q"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();
        var queues = (await containers.RabbitCtlAsync("list_queues", "name", "-q")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToHashSet();

        (string Queue, string Key)[] expected =
        [
            ("payment.refund-payment", PaymentMessages.RefundPayment),
            ("payment.cancel-payment", PaymentMessages.CancelPayment),
        ];
        foreach (var (queue, key) in expected)
        {
            Assert.Contains(bindings, b => b.Length >= 5 && b[0] == "payment.commands" && b[2] == queue && b[4] == key);
            Assert.Contains(queue, queues);
            Assert.Contains(MessageNames.DeadLetterQueue(queue), queues);
            foreach (var (delay, _) in MessageNames.RetryDelays) Assert.Contains(MessageNames.RetryQueue(queue, delay), queues);
        }
    }

    [Fact]
    public async Task Refund_through_the_broker_completes_once()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var completed = await QueueOnAsync(channel, "payment.events", PaymentMessages.RefundCompleted);
        var (orderId, _, _) = await Seed.PaidAsync(api, 1000);
        var (sentinelOrder, _, _) = await Seed.PaidAsync(api, 500);
        var command = new RefundPaymentV1(orderId, Guid.NewGuid(), 400, "return", null);
        var sentinel = new RefundPaymentV1(sentinelOrder, Guid.NewGuid(), 100, "return", null);
        var envelope = NewCommand(PaymentMessages.RefundPayment, command);

        await PublishAsync(channel, "payment.commands", envelope);
        await PublishAsync(channel, "payment.commands", envelope);
        await PublishAsync(channel, "payment.commands", NewCommand(PaymentMessages.RefundPayment, sentinel)); // queued after the duplicate, so its row proves the duplicate was handled
        await Poll.UntilAsync(async () => await RefundAsync(api, sentinel.RefundId) is not null, 20, "sentinel refund row");
        TickExecutor(worker);

        await Poll.UntilAsync(async () => (await RefundAsync(api, command.RefundId))?.Status == RefundStatuses.Succeeded
            && (await RefundAsync(api, sentinel.RefundId))?.Status == RefundStatuses.Succeeded, 20, "both refunds succeeded");
        var seen = new List<Guid>();
        for (var i = 0; i < 2; i++) seen.Add((await NextAsync(channel, completed, "refund-completed on payment.events")).GetPayload<RefundCompletedV1>().RefundId);
        Assert.Equivalent(new[] { command.RefundId, sentinel.RefundId }, seen);
        await using var db = api.NewDb();
        Assert.Equal(1, await db.Refunds.CountAsync(r => r.OrderId == orderId, Ct));
        Assert.Equal(2, api.Stripe.Requests.Count(r => r.Path == "v1/refunds" && r.Method == "POST")); // one Stripe refund per refund id
        Assert.Single(await Outbox.PayloadsAsync<RefundCompletedV1>(api, PaymentMessages.RefundCompleted), e => e.RefundId == command.RefundId);
    }

    [Fact]
    public async Task P07_over_limit_refund_lands_in_the_dlq()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var (orderId, paymentId, _) = await Seed.PaidAsync(api, 1000);
        var accepted = new RefundPaymentV1(orderId, Guid.NewGuid(), 700, "return", null);
        var over = new RefundPaymentV1(orderId, Guid.NewGuid(), 400, "return", null);

        await PublishAsync(channel, "payment.commands", NewCommand(PaymentMessages.RefundPayment, accepted));
        await Poll.UntilAsync(async () => await RefundAsync(api, accepted.RefundId) is not null, 20, "first refund row");
        await PublishAsync(channel, "payment.commands", NewCommand(PaymentMessages.RefundPayment, over));

        BasicGetResult? dead = null;
        await Poll.UntilAsync(async () => (dead = await channel.BasicGetAsync("payment.refund-payment.dlq", false, Ct)) is not null, 20, "the over-limit command in payment.refund-payment.dlq");
        Assert.Equal(over.RefundId, Envelope.Parse(dead!.Body.Span).GetPayload<RefundPaymentV1>().RefundId);
        await channel.BasicNackAsync(dead.DeliveryTag, false, true, Ct); // leave it there
        Assert.Null(await RefundAsync(api, over.RefundId));
        await using var db = api.NewDb();
        Assert.Equal(700, await db.Payments.Where(p => p.Id == paymentId).Select(p => p.RefundedCents).SingleAsync(Ct));
    }

    [Fact]
    public async Task Cancel_through_the_broker_cancels()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var (orderId, _, _) = await Seed.CreateAsync(api);

        await PublishAsync(channel, "payment.commands", NewCommand(PaymentMessages.CancelPayment, new CancelPaymentV1(orderId)));
        await using (var db = api.NewDb())
            await Poll.UntilAsync(async () => await db.Payments.AsNoTracking().AnyAsync(p => p.OrderId == orderId && p.CancelRequested, Ct), 15, "cancel requested");
        TickExecutor(worker);

        await Poll.UntilAsync(async () => (await api.PaymentByOrderAsync(orderId)).Status == PaymentStatuses.Canceled, 15, "payment canceled");
    }

    [Fact]
    public async Task Webhook_events_reach_payment_events()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var succeeded = await QueueOnAsync(channel, "payment.events", PaymentMessages.Succeeded);

        var (orderId, paymentId, _) = await Seed.PaidAsync(api, 1000); // the completed webhook goes to the API host; the worker's relay publishes

        var published = (await NextAsync(channel, succeeded, "payment.succeeded.v1 on payment.events")).GetPayload<PaymentSucceededV1>();
        Assert.Equal((orderId, paymentId, 1000L), (published.OrderId, published.PaymentId, published.AmountCents));
    }

    [Fact]
    public async Task Dlq_admin_is_hosted()
    {
        var (api, worker) = await StartAsync(); // the worker declares the queues
        await using var _ = api;
        await using var __ = worker;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dlq/payment/queues");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        request.Headers.Add("x-bloom-user-type", "staff");
        request.Headers.Add("x-bloom-role", "Admin"); // the value Bloom.Shared checks today
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());
        using var response = await api.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        foreach (var handler in new[] { "refund-payment", "cancel-payment" }) Assert.Contains($"payment.{handler}.dlq", body);
    }

    [Fact]
    public async Task Worker_runs_executor_and_jobs()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var hosted = worker.Services.GetServices<IHostedService>().ToList();
        Assert.Single(hosted.OfType<ProviderExecutor>());
        Assert.Single(hosted.OfType<Reconciler>());
        Assert.Single(hosted.OfType<NightlyJob>());
        var apiHosted = api.Services.GetServices<IHostedService>().ToList();
        Assert.Empty(apiHosted.OfType<ProviderExecutor>());
        Assert.Empty(apiHosted.OfType<Reconciler>());
        Assert.Empty(apiHosted.OfType<NightlyJob>());
    }
}
