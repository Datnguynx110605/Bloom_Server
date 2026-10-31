using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bloom.Identity.Contracts;
using RabbitMQ.Client;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Bloom.Gateway.Tests;

// One collection for every test that starts a worker: they all consume the one queue (competing consumers).
[Collection("gateway-worker")]
public class RevocationWorkerTests(Containers containers)
{
    private const string Exchange = "identity.events";
    private const string RoutingKey = "identity.tokens-revoked.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDatabase Db => containers.Redis.GetDatabase();

    [Fact]
    public async Task Worker_declares_queue_and_binding()
    {
        await using var worker = await TestWorker.StartAsync(containers);

        Assert.Equal(["quorum", "true"], await TestWorker.QueueAsync(containers, "type", "durable"));
        Assert.Contains("identity.events\ttopic\ttrue", await containers.RabbitCtlAsync("list_exchanges", "--silent", "name", "type", "durable"));
        Assert.Contains($"identity.events\texchange\t{TestWorker.Queue}\tqueue\t{RoutingKey}",
            await containers.RabbitCtlAsync("list_bindings", "--silent", "source_name", "source_kind", "destination_name", "destination_kind", "routing_key"));
    }

    [Fact]
    public async Task G04_latest_issuedBefore_wins()
    {
        await using var worker = await TestWorker.StartAsync(containers);
        var id = Guid.NewGuid();

        await PublishAsync(TestWorker.RevocationEnvelope(id, 2000));
        await PublishAsync(TestWorker.RevocationEnvelope(id, 1000));
        await PublishAsync(TestWorker.RevocationEnvelope(id, 2000));
        await AllProcessedAsync();

        Assert.Equal("2000", (string?)await Db.StringGetAsync($"rev:{id:D}"));
        var ttl = (await Db.KeyTimeToLiveAsync($"rev:{id:D}"))!.Value.TotalMilliseconds;
        Assert.InRange(ttl, 950_001, 960_000);
    }

    [Fact]
    public async Task G03_revocation_reaches_the_gateway_within_seconds()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        await using var worker = await TestWorker.StartAsync(containers);
        var id = Guid.NewGuid();
        var token = TestTokens.Issue(gateway.DefaultKey, sub: id);
        using (var before = await GetCartAsync(gateway, token))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // The token's iat comes from the gateway's frozen clock; the mark must lie after it.
        var issuedBefore = gateway.Clock.GetUtcNow().ToUnixTimeMilliseconds() + 1000;
        var started = DateTime.UtcNow;
        await PublishAsync(TestWorker.RevocationEnvelope(id, issuedBefore));
        await Poll.UntilAsync(async () =>
        {
            using var response = await GetCartAsync(gateway, token);
            return response.StatusCode == HttpStatusCode.Unauthorized;
        }, 2, "GET /api/cart is 401");

        using var revoked = await GetCartAsync(gateway, token);
        Assert.Contains("TOKEN_REVOKED", await revoked.Content.ReadAsStringAsync(Ct));
        TestContext.Current.SendDiagnosticMessage($"G03: revocation reached the gateway in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("no-payload")]
    [InlineData("unknown-type")]
    [InlineData("missing-issuedBefore")]
    public async Task Malformed_messages_are_acked_and_skipped(string kind)
    {
        await using var worker = await TestWorker.StartAsync(containers);
        var id = Guid.NewGuid();
        var envelope = JsonSerializer.SerializeToElement(new { accountId = id, issuedBefore = 5000 }, JsonSerializerOptions.Web);
        byte[] body = kind switch
        {
            "not-json" => "not-json"u8.ToArray(),
            "no-payload" => Encoding.UTF8.GetBytes($$"""{"messageId":"{{Guid.NewGuid()}}","messageType":"{{RoutingKey}}","kind":"event","producer":"identity","occurredAt":"2026-11-01T00:00:00Z","correlationId":"{{Guid.NewGuid()}}"}"""),
            "unknown-type" => TestWorker.Build(id, "identity.something-else.v1", envelope),
            _ => TestWorker.Build(id, RoutingKey, JsonSerializer.SerializeToElement(new { accountId = id }, JsonSerializerOptions.Web)),
        };

        await PublishAsync(body);
        await AllProcessedAsync();

        Assert.False(await Db.KeyExistsAsync($"rev:{id:D}"));
    }

    [Fact]
    public async Task Redis_outage_holds_the_delivery_until_written()
    {
        await using var redis = new RedisBuilder("redis:8.2.3").Build();
        await redis.StartAsync(Ct);
        var connection = $"{redis.GetConnectionString()},asyncTimeout=500"; // a paused container ends commands by timing out; keep that short
        await using var inspector = await ConnectionMultiplexer.ConnectAsync(connection);
        await using var worker = await TestWorker.StartAsync(containers, connection);
        var id = Guid.NewGuid();

        await redis.PauseAsync(Ct);
        try
        {
            await PublishAsync(TestWorker.RevocationEnvelope(id, 3000));
            await Poll.UntilAsync(async () => (await TestWorker.QueueAsync(containers, "messages_unacknowledged")).FirstOrDefault() == "1", 15, "one delivery is unacknowledged");
            await Task.Delay(2000, Ct);
            Assert.Equal(["0", "1"], await TestWorker.QueueAsync(containers, "messages_ready", "messages_unacknowledged"));
        }
        finally
        {
            await redis.UnpauseAsync(Ct);
        }

        await Poll.UntilAsync(async () => (string?)await inspector.GetDatabase().StringGetAsync($"rev:{id:D}") == "3000", 15, "the mark is written");
        await Poll.UntilAsync(async () => (await TestWorker.QueueAsync(containers, "messages")).FirstOrDefault() == "0", 15, "the queue is drained");
    }

    [Fact]
    public async Task Worker_reconnects_after_the_broker_closes_its_connection()
    {
        await using var worker = await TestWorker.StartAsync(containers);
        var id = Guid.NewGuid();

        await containers.RabbitCtlAsync("close_all_connections", "test");
        await PublishAsync(TestWorker.RevocationEnvelope(id, 4000));

        await Poll.UntilAsync(async () => (string?)await Db.StringGetAsync($"rev:{id:D}") == "4000", 15, "the mark is written after the reconnect");
    }

    private async Task PublishAsync(byte[] body)
    {
        await using var connection = await new ConnectionFactory { Uri = new Uri(containers.RabbitMqUri) }.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), Ct);
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: Ct);
        await channel.BasicPublishAsync(Exchange, RoutingKey, false, new BasicProperties { DeliveryMode = DeliveryModes.Persistent }, body, Ct);
    }

    /// <summary>Publishes a valid message for a fresh account and waits for its mark and an empty queue: the worker has handled every message published before it.</summary>
    private async Task AllProcessedAsync()
    {
        var sentinel = Guid.NewGuid();
        await PublishAsync(TestWorker.RevocationEnvelope(sentinel, 1));
        await Poll.UntilAsync(async () => await Db.KeyExistsAsync($"rev:{sentinel:D}"), 15, "the worker has caught up");
        await Poll.UntilAsync(async () => (await TestWorker.QueueAsync(containers, "messages")).FirstOrDefault() == "0", 15, "the queue is drained");
    }

    private static Task<HttpResponseMessage> GetCartAsync(TestGateway gateway, string token) =>
        gateway.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/cart") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } }, Ct);
}
