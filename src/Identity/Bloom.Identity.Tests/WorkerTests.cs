using System.Text.Json;
using Bloom.Identity.Audit;
using Bloom.Identity.Contracts;
using Bloom.Identity.Data;
using Bloom.Identity.Worker;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;

namespace Bloom.Identity.Tests;

public class WorkerTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Producers = ["catalog", "inventory", "launch", "order", "payment", "fulfillment", "identity", "notification"];

    private async Task<(TestIdentity Api, TestIdentity Worker)> StartAsync()
    {
        var api = await TestIdentity.StartAsync(containers);
        var worker = await TestIdentity.StartAsync(containers, o => { o.Mode = "worker"; o.ConnectionString = api.ConnectionString; });
        return (api, worker);
    }

    private async Task<(IConnection Connection, IChannel Channel)> RabbitAsync()
    {
        var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync(Ct);
        return (connection, await connection.CreateChannelAsync(cancellationToken: Ct));
    }

    private static Envelope AuditEnvelope(Guid id, string service, JsonElement? payload = null)
    {
        var ticks = DateTime.UtcNow.Ticks;
        var occurredAt = new DateTime(ticks - ticks % 10, DateTimeKind.Utc); // PostgreSQL keeps microseconds
        payload ??= JsonSerializer.SerializeToElement(new
        {
            actorId = Guid.NewGuid(), actorRole = "admin", service, action = "replay", targetType = "dead_letter", targetId = "q/1",
            details = new { queue = $"{service}.h", count = 2 },
        }, JsonSerializerOptions.Web);
        return new Envelope(id, IdentityMessages.StaffActionRecorded, "event", service, occurredAt, Guid.NewGuid(), payload.Value);
    }

    private static Task PublishAsync(IChannel channel, string exchange, Envelope e) =>
        channel.BasicPublishAsync(exchange, e.MessageType, false,
            new BasicProperties { MessageId = e.MessageId.ToString(), Type = e.MessageType, DeliveryMode = DeliveryModes.Persistent }, e.ToUtf8Bytes()).AsTask();

    private static async Task<int> CountAsync(TestIdentity t, Guid id)
    {
        await using var db = t.NewDb();
        return await db.AuditEntries.CountAsync(a => a.Id == id, Ct);
    }

    private static Task AuditRowAsync(TestIdentity t, Guid id) =>
        Poll.UntilAsync(async () => await CountAsync(t, id) == 1, 20, $"audit row {id}");

    [Fact]
    public async Task Outbox_rows_reach_identity_events()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var queue = $"test-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: true, cancellationToken: Ct);
        await channel.QueueBindAsync(queue, "identity.events", IdentityMessages.TokensRevoked, cancellationToken: Ct);
        var account = await Seed.CustomerAsync(api);

        var response = await api.SendAsync(HttpMethod.Post, "/api/me/sign-out-everywhere", caller: TestCaller.Customer(account.Id, api.Clock.GetUtcNow()));

        Assert.True(response.IsSuccessStatusCode);
        var sent = (await Outbox.EnvelopesAsync(api, IdentityMessages.TokensRevoked)).Single();
        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync(queue, true, Ct)) is not null, 20, "tokens-revoked on identity.events");
        Assert.Equal(sent.MessageId, Envelope.Parse(got!.Body.Span).MessageId);
        await using var db = api.NewDb();
        await Poll.UntilAsync(async () => (await db.Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == sent.MessageId, Ct)).Status == "sent", 20, "outbox row sent");
    }

    [Fact]
    public async Task Audit_queue_is_bound_on_all_eight_exchanges()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;

        var bindings = (await containers.RabbitCtlAsync("list_bindings", "-q"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();

        foreach (var svc in Producers)
            Assert.Contains(bindings, b => b.Length >= 5 && b[0] == $"{svc}.events" && b[2] == "identity.audit" && b[4] == IdentityMessages.StaffActionRecorded);
    }

    [Fact]
    public async Task ID_T16_same_staff_action_event_twice_gives_one_audit_row()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        using var metrics = new MetricCapture(worker.Services);
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var e = AuditEnvelope(Guid.CreateVersion7(), "catalog");
        var sentinel = AuditEnvelope(Guid.CreateVersion7(), "inventory"); // queued after the duplicates, so its row proves they were handled

        await PublishAsync(channel, "catalog.events", e);
        await PublishAsync(channel, "catalog.events", e);
        await PublishAsync(channel, "inventory.events", sentinel);
        await AuditRowAsync(api, sentinel.MessageId);

        await using var db = api.NewDb();
        var row = await db.AuditEntries.SingleAsync(a => a.Id == e.MessageId, Ct);
        var p = e.GetPayload<StaffActionRecorded>();
        Assert.Equal(new DateTimeOffset(e.OccurredAt), row.OccurredAt);
        Assert.Equal((p.ActorId, p.ActorRole, p.Service, p.Action, p.TargetType, p.TargetId), (row.ActorId, row.ActorRole, row.Service, row.Action, row.TargetType, row.TargetId));
        Assert.Equal("""{"count": 2, "queue": "catalog.h"}""", row.Details);
        Assert.Equal(1, metrics.Sum("audit_entries_total", ("service", "catalog")));

        // The handler itself is idempotent too (ON CONFLICT), whatever the inbox does.
        var again = AuditEnvelope(Guid.CreateVersion7(), "order");
        await using var scope = worker.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<AuditConsumer>();
        await handler.HandleAsync(again, Ct);
        await handler.HandleAsync(again, Ct);
        await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().SaveChangesAsync(Ct);
        Assert.Equal(1, await CountAsync(api, again.MessageId));
        Assert.Equal(1, metrics.Sum("audit_entries_total", ("service", "order")));
    }

    [Fact]
    public async Task Invalid_audit_payload_is_dead_lettered()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var (connection, channel) = await RabbitAsync();
        await using var ___ = connection;
        await using var ____ = channel;
        var e = AuditEnvelope(Guid.CreateVersion7(), "catalog", JsonDocument.Parse("{}").RootElement.Clone());

        await PublishAsync(channel, "catalog.events", e);

        BasicGetResult? got = null;
        await Poll.UntilAsync(async () => (got = await channel.BasicGetAsync("identity.audit.dlq", true, Ct)) is not null, 20, "message in identity.audit.dlq");
        Assert.Equal(e.MessageId, Envelope.Parse(got!.Body.Span).MessageId);
        Assert.Equal(0, await CountAsync(api, e.MessageId));
    }

    [Fact]
    public async Task Cleanup_removes_rows_a_day_past_expiry()
    {
        var (api, worker) = await StartAsync();
        await using var _ = api;
        await using var __ = worker;
        var now = worker.Clock.GetUtcNow();
        var old = now.AddDays(-1).AddSeconds(-1);
        var recent = now.AddHours(-23);
        var account = await Seed.CustomerAsync(api);
        var audit = AuditEnvelope(Guid.CreateVersion7(), "catalog");
        await using (var db = api.NewDb())
        {
            foreach (var (expiresAt, kept) in new[] { (old, false), (recent, true) })
            {
                var hash = kept ? (byte)1 : (byte)2;
                db.VerificationCodes.Add(new VerificationCode { Id = Guid.CreateVersion7(), Purpose = CodePurposes.SignUp, TargetEmail = "a@example.com", CodeHmac = [hash], ExpiresAt = expiresAt, CreatedAt = now });
                db.StaffInvitations.Add(new StaffInvitation { Id = Guid.CreateVersion7(), EmailNormalized = "s@bloom.example", Role = StaffRoles.Support, TokenHash = [hash], InvitedBy = account.Id, ExpiresAt = expiresAt });
                db.RefreshTokens.Add(new RefreshToken { Id = Guid.CreateVersion7(), FamilyId = Guid.NewGuid(), AccountId = account.Id, TokenHash = [hash], Status = RefreshStatuses.Active, ExpiresAt = expiresAt, CreatedAt = now, AuthTime = now });
            }
            db.AuditEntries.Add(new AuditEntry { Id = audit.MessageId, OccurredAt = old, ActorId = account.Id, ActorRole = "admin", Service = "catalog", Action = "replay", TargetType = "t", TargetId = "1", Details = "{}" });
            await db.SaveChangesAsync(Ct);
        }

        await worker.Services.GetServices<IHostedService>().OfType<CleanupJob>().Single().RunOnceAsync(Ct);

        await using var check = api.NewDb();
        Assert.Equal(recent, (await check.VerificationCodes.SingleAsync(Ct)).ExpiresAt);
        Assert.Equal(recent, (await check.StaffInvitations.SingleAsync(Ct)).ExpiresAt);
        Assert.Equal(recent, (await check.RefreshTokens.SingleAsync(Ct)).ExpiresAt);
        Assert.Equal(1, await check.AuditEntries.CountAsync(Ct));
    }
}
