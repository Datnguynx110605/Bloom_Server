using Bloom.Notification.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Bloom.Notification.Tests;

/// <summary>09-notification §4.2, §6.3, §9 over SignalR: ticket and Origin gate, 15-minute lifetime, cross-pod disconnect (spec §7 Sockets, owner item 1).</summary>
public class HubTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<PushMessage> PushAsync(TestNotification t, Guid accountId, string title = "Order paid")
    {
        var message = new PushMessage(Guid.NewGuid(), "order.paid", title, "/orders/BL-1", DateTimeOffset.UtcNow);
        await t.Services.GetRequiredService<IHubContext<NotificationHub>>().Clients.Group(NotificationHub.Group(accountId)).SendAsync(NotificationHub.ClientMethod, message, Ct);
        return message;
    }

    [Fact]
    public async Task Ticket_connects_and_joins_the_users_group()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var account = Guid.NewGuid();
        await using var c = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, account));
        var inbox = Sockets.Inbox(c);
        Assert.Equal(1, metrics.Sum("ws_connections"));

        var sent = await PushAsync(t, account);
        var got = await Sockets.NextAsync(inbox);
        Assert.Equal(sent.Id, got.Id);
        Assert.Equal("order.paid", got.Type);
        Assert.Equal("Order paid", got.Title);
        Assert.Equal("/orders/BL-1", got.Link);
        Assert.Equal(sent.CreatedAt, got.CreatedAt);

        await c.StopAsync(Ct);
        await Poll.UntilAsync(() => Task.FromResult(metrics.Sum("ws_connections") == 0), 5, "ws_connections back to 0");
    }

    [Fact]
    public async Task N04_reused_ticket_is_rejected()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var ticket = await Sockets.TicketAsync(t, Guid.NewGuid());
        await using var c = await Sockets.ConnectAsync(t, ticket);

        Assert.Equal(401, await Sockets.StatusAsync(t, ticket));
        await Assert.ThrowsAnyAsync<Exception>(() => Sockets.ConnectAsync(t, ticket));
        Assert.Equal(2, metrics.Sum("ws_rejected_total", ("reason", "ticket")));
    }

    [Fact]
    public async Task Unknown_or_missing_ticket_is_rejected()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        Assert.Equal(401, await Sockets.StatusAsync(t, "no-such-ticket"));
        Assert.Equal(401, await Sockets.StatusAsync(t, null));
        Assert.Equal(2, metrics.Sum("ws_rejected_total", ("reason", "ticket")));
    }

    [Fact]
    public async Task One_ticket_connects_once_under_concurrency()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var ticket = await Sockets.TicketAsync(t, Guid.NewGuid());

        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            try { return (Connection: await Sockets.ConnectAsync(t, ticket), Error: (Exception?)null); }
            catch (Exception e) { return (Connection: (HubConnection?)null, Error: e); }
        }));

        var connected = attempts.Where(a => a.Connection is not null).ToArray();
        Assert.Single(connected);
        Assert.Equal(HubConnectionState.Connected, connected[0].Connection!.State);
        var losers = attempts.Where(a => a.Error is not null).Select(a => a.Error!).ToArray();
        Assert.Equal(9, losers.Length);
        Assert.All(losers, e => Assert.Contains("401", e.Message)); // "Incomplete handshake, status code: 401"
        Assert.Equal(9, metrics.Sum("ws_rejected_total", ("reason", "ticket")));
        await connected[0].Connection!.DisposeAsync();
    }

    [Fact]
    public async Task Disallowed_origin_is_rejected_and_keeps_the_ticket()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var ticket = await Sockets.TicketAsync(t, Guid.NewGuid());

        Assert.Equal(403, await Sockets.StatusAsync(t, ticket, "https://evil.test"));
        Assert.Equal(1, metrics.Sum("ws_rejected_total", ("reason", "origin")));
        Assert.Equal(403, await Sockets.StatusAsync(t, ticket, null));
        Assert.Equal(2, metrics.Sum("ws_rejected_total", ("reason", "origin")));
        Assert.Equal(0, metrics.Sum("ws_rejected_total", ("reason", "ticket")));

        await using var c = await Sockets.ConnectAsync(t, ticket, "https://SHOP.test");
        Assert.Equal(HubConnectionState.Connected, c.State);
    }

    [Fact]
    public async Task Non_websocket_request_is_400_and_keeps_the_ticket()
    {
        await using var t = await TestNotification.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var ticket = await Sockets.TicketAsync(t, Guid.NewGuid());

        var response = await t.Http.GetAsync($"{NotificationHub.Path}?ticket={ticket}", Ct);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync(Ct));
        Assert.True(await t.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().KeyExistsAsync($"ws:ticket:{ticket}"));
        await using var c = await Sockets.ConnectAsync(t, ticket);
        Assert.Equal(0, metrics.Sum("ws_rejected_total"));
    }

    [Fact]
    public async Task Every_path_under_the_hub_is_gated()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var ticket = await Sockets.TicketAsync(t, Guid.NewGuid());

        var negotiate = await t.Http.PostAsync($"{NotificationHub.Path}/negotiate?ticket={ticket}", null, Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, negotiate.StatusCode);
        Assert.Equal(401, await Sockets.StatusAsync(t, null, path: NotificationHub.Path + "/"));
        Assert.Equal(403, await Sockets.StatusAsync(t, ticket, "https://evil.test", NotificationHub.Path.ToUpperInvariant()));
        Assert.True(await t.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().KeyExistsAsync($"ws:ticket:{ticket}"));
    }

    [Fact]
    public async Task Kestrel_accepts_a_real_websocket_upgrade()
    {
        // TestServer supplies the WebSocket feature itself, Kestrel only through UseWebSockets(): run the gate on the real server too.
        var config = TestNotification.DefaultConfig(await containers.CreateDatabaseAsync(), containers.RabbitMqUri, containers.RedisConnectionString, containers.SmtpHost, containers.SmtpPort.ToString());
        config["Notification:Mode"] = "api";
        await using var app = NotificationApp.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(config);
            b.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
        });
        await app.StartAsync(Ct);
        var url = app.Urls.Single();
        var ticket = await app.Services.GetRequiredService<Tickets>().IssueAsync(Guid.NewGuid());

        await using var c = new HubConnectionBuilder()
            .WithUrl($"{url}{NotificationHub.Path}?ticket={ticket}", o =>
            {
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.WebSocketConfiguration = ws => ws.SetRequestHeader("Origin", Sockets.Origin);
            })
            .Build();
        await c.StartAsync(Ct);

        Assert.Equal(HubConnectionState.Connected, c.State);
        await c.StopAsync(Ct);
    }

    [Fact]
    public async Task N05_connection_is_closed_after_15_minutes()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        await using var c = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, account));

        t.Clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        await Task.Delay(300, Ct);
        Assert.Equal(HubConnectionState.Connected, c.State);

        t.Clock.Advance(TimeSpan.FromSeconds(2));
        await Sockets.ClosedAsync(c, TimeSpan.FromSeconds(2));

        await using var again = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, account)); // the client's reconnect
        Assert.Equal(HubConnectionState.Connected, again.State);
    }

    [Fact]
    public async Task Every_tab_gets_the_push()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var account = Guid.NewGuid();
        await using var one = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, account));
        await using var two = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, account));
        var inboxOne = Sockets.Inbox(one);
        var inboxTwo = Sockets.Inbox(two);

        var sent = await PushAsync(t, account);

        Assert.Equal(sent.Id, (await Sockets.NextAsync(inboxOne)).Id);
        Assert.Equal(sent.Id, (await Sockets.NextAsync(inboxTwo)).Id);
    }

    [Fact]
    public async Task Disconnect_message_closes_only_that_users_sockets()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using var a1 = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, a));
        await using var a2 = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, a));
        await using var b1 = await Sockets.ConnectAsync(t, await Sockets.TicketAsync(t, b));

        await Disconnects.PublishAsync(t.Services.GetRequiredService<IConnectionMultiplexer>(), a);

        await Sockets.ClosedAsync(a1, TimeSpan.FromSeconds(2));
        await Sockets.ClosedAsync(a2, TimeSpan.FromSeconds(2));
        Assert.Equal(HubConnectionState.Connected, b1.State);
    }
}
