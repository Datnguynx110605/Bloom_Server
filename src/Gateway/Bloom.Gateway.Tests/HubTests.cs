using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Bloom.Gateway.Tests;

public class HubTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hub_upgrade_with_allowed_origin_is_proxied_without_JWT()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var socket = await ConnectAsync(gateway, "https://bloom.test");

        Assert.Equal("ping", await EchoAsync(socket, "ping"));
    }

    [Fact]
    public async Task Hub_upgrade_from_foreign_origin_is_403()
    {
        await using var gateway = await TestGateway.StartAsync(containers);

        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(gateway, "https://evil.test"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/notifications/hub?ticket=t1");
        request.Headers.Add("Origin", "https://evil.test");
        using var response = await gateway.Client.SendAsync(request, Ct);
        await CorsOriginTests.AssertOriginRejectedAsync(response);
        Assert.Equal(0, gateway.Upstream.RequestCount);
    }

    [Fact]
    public async Task Hub_connection_outlives_the_route_timeout()
    {
        await using var gateway = await TestGateway.StartAsync(containers);
        using var socket = await ConnectAsync(gateway, "https://bloom.test");

        await Task.Delay(TimeSpan.FromSeconds(7), Ct); // longer than the 5 s default route timeout

        Assert.Equal("still here", await EchoAsync(socket, "still here"));
    }

    private static async Task<ClientWebSocket> ConnectAsync(TestGateway gateway, string origin)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            await socket.ConnectAsync(new UriBuilder(gateway.Address) { Scheme = "ws", Path = "/api/notifications/hub", Query = "ticket=t1" }.Uri, Ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<string> EchoAsync(ClientWebSocket socket, string text)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Ct);
        var buffer = new byte[256];
        var frame = await socket.ReceiveAsync(buffer, Ct);
        return Encoding.UTF8.GetString(buffer, 0, frame.Count);
    }
}
