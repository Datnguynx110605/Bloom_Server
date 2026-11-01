using System.Text.Json;
using Bloom.Identity.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Bloom.Gateway.Tests;

/// <summary>The gateway in worker mode (<see cref="GatewayApp.Build"/>), consuming from the test RabbitMQ and writing to <c>redis</c>.</summary>
public sealed class TestWorker : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestWorker(WebApplication app) => _app = app;

    /// <summary>Returns once the worker is consuming, so a message published next is not lost for want of a binding.</summary>
    /// <param name="redis">Redis connection string; the shared test Redis when null.</param>
    public static async Task<TestWorker> StartAsync(Containers containers, string? redis = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["Gateway:Mode"] = "worker",
            ["Gateway:Redis"] = redis ?? containers.RedisConnectionString,
            ["Gateway:RabbitMq"] = containers.RabbitMqUri,
            ["urls"] = "http://127.0.0.1:0",
        };
        var app = GatewayApp.Build([], b => b.Configuration.AddInMemoryCollection(config));
        await app.StartAsync();
        var worker = new TestWorker(app);
        await Poll.UntilAsync(async () => (await QueueAsync(containers, "consumers")).FirstOrDefault() == "1", 15, "the worker is consuming");
        return worker;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    /// <summary>The worker queue's values for the named <c>rabbitmqctl list_queues</c> columns; empty when the queue does not exist.</summary>
    public static async Task<string[]> QueueAsync(Containers containers, params string[] columns)
    {
        var output = await containers.RabbitCtlAsync(["list_queues", "--silent", "name", .. columns]);
        var row = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .FirstOrDefault(cells => cells[0] == RevocationConsumer.Queue);
        return row?[1..] ?? [];
    }

    public static byte[] RevocationEnvelope(Guid accountId, long issuedBefore) =>
        Build(accountId, "identity.tokens-revoked.v1", JsonSerializer.SerializeToElement(new TokensRevokedV1(accountId, issuedBefore), JsonSerializerOptions.Web));

    internal static byte[] Build(Guid accountId, string messageType, JsonElement payload) =>
        new Envelope(Guid.CreateVersion7(), messageType, MessageKinds.Event, "identity", DateTime.UtcNow, accountId, payload).ToUtf8Bytes();
}
