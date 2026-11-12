using System.Net;
using System.Text.Json;
using Bloom.Catalog.Data;
using Bloom.Catalog.Worker;
using Bloom.Identity.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Contracts;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Catalog.Tests;

public class WorkerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestCatalog Api, TestCatalog Worker)> StartAsync()
    {
        var api = await TestCatalog.StartAsync(containers);
        var worker = await TestCatalog.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api.ConnectionString; });
        return (api, worker);
    }

    private async Task<(IConnection Connection, IChannel Channel)> RabbitAsync()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync(Ct);
        return (connection, await connection.CreateChannelAsync(cancellationToken: Ct));
    }

    private static Envelope StageEnvelope(Guid launchId, string stage, Guid productId) =>
        new(Guid.CreateVersion7(), LaunchMessages.StageChanged, MessageKinds.Event, "launch", DateTime.UtcNow, Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new LaunchStageChangedV1(launchId, stage, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(30),
                [new LaunchProductV1(productId, 2)], []), JsonSerializerOptions.Web));

    private static Task PublishAsync(IChannel channel, string exchange, Envelope e) =>
        channel.BasicPublishAsync(exchange, e.MessageType, false,
            new BasicProperties { MessageId = e.MessageId.ToString(), Type = e.MessageType, DeliveryMode = DeliveryModes.Persistent }, e.ToUtf8Bytes()).AsTask();

    [Fact]
    public async Task Audit_events_reach_catalog_events()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var queue = $"test-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(queue, "catalog.events", IdentityMessages.StaffActionRecorded, cancellationToken: Ct);

        var response = await api.SendAsync(HttpMethod.Post, "/api/admin/catalog/categories", new { name = "Phones", slug = "phones", sortOrder = 0 }, TestCaller.Staff("catalog_manager"));

        await response.OkJsonAsync();
        var sent = Assert.Single(await Outbox.AuditAsync(api));
        Assert.Equal("catalog.category-created", sent.Action);
        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync(queue, true, Ct)) is not null, 20, "staff-action-recorded on catalog.events");
        var envelope = Envelope.Parse(got!.Body.Span);
        Assert.Equal("catalog.category-created", envelope.GetPayload<StaffActionRecorded>().Action);
        await using var db = api.NewDb();
        await Poll.UntilAsync(async () => (await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == envelope.MessageId, Ct)).Status == "sent", 20, "outbox row sent");
    }

    [Fact]
    public async Task Five_queues_are_bound()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var bindings = (await containers.RabbitCtlAsync("list_bindings", "-q"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();

        (string Queue, string Exchange, string Key)[] expected =
        [
            ("catalog.launch-stage-changed", "launch.events", LaunchMessages.StageChanged),
            ("catalog.order-delivered", "order.events", OrderMessages.Delivered),
            ("catalog.customer-registered", "identity.events", IdentityMessages.CustomerRegistered),
            ("catalog.contact-changed", "identity.events", IdentityMessages.ContactChanged),
            ("catalog.account-deleted", "identity.events", IdentityMessages.AccountDeleted),
        ];
        foreach (var (queue, exchange, key) in expected)
            Assert.Contains(bindings, b => b.Length >= 5 && b[0] == exchange && b[2] == queue && b[4] == key);
    }

    [Fact]
    public async Task Published_launch_event_is_applied_once()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var category = await Seed.CategoryAsync(api);
        var phone = await Seed.PhoneAsync(api, category.Id, status: ProductStatuses.Draft);
        var other = await Seed.PhoneAsync(api, category.Id, status: ProductStatuses.Draft);
        var e = StageEnvelope(Guid.NewGuid(), "released", phone.Product.Id);
        var sentinel = StageEnvelope(Guid.NewGuid(), "released", other.Product.Id); // queued after the duplicates, so its effect proves they were handled

        await PublishAsync(channel, "launch.events", e);
        await PublishAsync(channel, "launch.events", e);
        await PublishAsync(channel, "launch.events", sentinel);

        await using var db = api.NewDb();
        await Poll.UntilAsync(async () => (await db.Products.AsNoTracking().SingleAsync(p => p.Id == other.Product.Id, Ct)).Status == ProductStatuses.Active, 20, "sentinel applied");
        Assert.Equal(ProductStatuses.Active, (await db.Products.AsNoTracking().SingleAsync(p => p.Id == phone.Product.Id, Ct)).Status);
        Assert.Equal(1, await db.Set<InboxMessage>().CountAsync(m => m.MessageId == e.MessageId, Ct));
    }

    [Fact]
    public async Task Invalid_payload_is_dead_lettered()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var e = StageEnvelope(Guid.NewGuid(), "released", Guid.NewGuid()) with { Payload = JsonDocument.Parse("{}").RootElement.Clone() };

        await PublishAsync(channel, "launch.events", e);

        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync("catalog.launch-stage-changed.dlq", true, Ct)) is not null, 20, "message in catalog.launch-stage-changed.dlq");
        Assert.Equal(e.MessageId, Envelope.Parse(got!.Body.Span).MessageId);
        await using var db = api.NewDb();
        Assert.Equal(0, await db.Set<InboxMessage>().CountAsync(m => m.MessageId == e.MessageId, Ct));
    }

    [Fact]
    public async Task Dlq_admin_is_hosted()
    {
        var (api, worker) = await StartAsync(); // the worker declares the queues
        await using var _ = api;
        await using var __ = worker;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/dlq/catalog/queues");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString());
        request.Headers.Add("x-bloom-user-type", "staff");
        request.Headers.Add("x-bloom-role", "admin");
        request.Headers.Add("x-bloom-request-id", Guid.NewGuid().ToString());
        using var response = await api.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        foreach (var handler in new[] { "launch-stage-changed", "order-delivered", "customer-registered", "contact-changed", "account-deleted" })
            Assert.Contains($"catalog.{handler}.dlq", body);
    }

    [Fact]
    public async Task Worker_runs_the_image_cleanup_job()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        Assert.Single(worker.Services.GetServices<IHostedService>().OfType<ImageCleanupJob>());
        Assert.Empty(api.Services.GetServices<IHostedService>().OfType<ImageCleanupJob>());
    }
}
