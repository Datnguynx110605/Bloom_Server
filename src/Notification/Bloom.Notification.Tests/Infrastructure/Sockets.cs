using System.Net.WebSockets;
using System.Threading.Channels;
using Bloom.Notification.Sockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Notification.Tests;

/// <summary>SignalR clients over <c>TestServer</c> WebSockets (spec §8): no negotiation, so the ticket is spent by the upgrade itself.</summary>
public static class Sockets
{
    public const string Origin = "https://shop.test";

    private static Uri Url(TestNotification t, string? ticket) =>
        new(t.Server.BaseAddress, ticket is null ? NotificationHub.Path : $"{NotificationHub.Path}?ticket={ticket}");

    private static Task<WebSocket> OpenAsync(TestNotification t, Uri uri, string? origin, CancellationToken ct)
    {
        var ws = t.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => { if (origin is not null) r.Headers.Origin = origin; };
        return ws.ConnectAsync(uri, ct);
    }

    public static Task<string> TicketAsync(TestNotification t, Guid accountId) => t.Services.GetRequiredService<Tickets>().IssueAsync(accountId);

    /// <summary>Starts a hub connection; throws when the gate or the upgrade refuses it.</summary>
    public static async Task<HubConnection> ConnectAsync(TestNotification t, string ticket, string? origin = Origin)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(Url(t, ticket), o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.WebSocketFactory = (_, ct) => new ValueTask<WebSocket>(OpenAsync(t, Url(t, ticket), origin, ct));
            })
            .Build();
        var registry = t.Services.GetRequiredService<ConnectionRegistry>();
        var before = registry.Count;
        await connection.StartAsync(TestContext.Current.CancellationToken);
        // The client is connected once the handshake is answered, which is before the hub has joined the group and registered the socket.
        await Poll.UntilAsync(() => Task.FromResult(registry.Count > before), 5, "the hub to register the socket");
        return connection;
    }

    /// <summary>The HTTP status the hub path answers a WebSocket request with (101 when it upgrades); the socket is closed again.</summary>
    public static async Task<int> StatusAsync(TestNotification t, string? ticket, string? origin = Origin)
    {
        try
        {
            using var ws = await OpenAsync(t, Url(t, ticket), origin, TestContext.Current.CancellationToken);
            return 101;
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("Incomplete handshake, status code: ", StringComparison.Ordinal))
        {
            return int.Parse(e.Message["Incomplete handshake, status code: ".Length..]);
        }
    }

    /// <summary>Everything the connection receives as <c>notification</c>, in order.</summary>
    public static Channel<PushMessage> Inbox(HubConnection c)
    {
        var inbox = Channel.CreateUnbounded<PushMessage>();
        c.On<PushMessage>(NotificationHub.ClientMethod, m => inbox.Writer.TryWrite(m));
        return inbox;
    }

    public static async Task<PushMessage> NextAsync(Channel<PushMessage> inbox)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        return await inbox.Reader.ReadAsync(cts.Token);
    }

    /// <summary>Waits for the server to close the connection; fails when it is still open after <paramref name="within"/> (real time).</summary>
    public static async Task ClosedAsync(HubConnection c, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (c.State != HubConnectionState.Disconnected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Connection still {c.State} after {within}");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
