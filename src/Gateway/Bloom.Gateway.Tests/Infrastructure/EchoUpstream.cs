using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bloom.Gateway.Tests;

/// <summary>
/// Stands in for every upstream service. Echoes what it received; <c>x-test-status</c> and <c>x-test-delay-ms</c>
/// request headers make it fail or stall; a WebSocket upgrade echoes text frames.
/// </summary>
public sealed class EchoUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _requestCount;

    private EchoUpstream(WebApplication app)
    {
        _app = app;
        app.UseWebSockets();
        app.Run(HandleAsync);
    }

    public Uri Address { get; private set; } = null!;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public static async Task<EchoUpstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var echo = new EchoUpstream(builder.Build());
        await echo._app.StartAsync();
        echo.Address = new Uri(echo._app.Urls.Single());
        return echo;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task HandleAsync(HttpContext context)
    {
        Interlocked.Increment(ref _requestCount);
        var request = context.Request;

        if (context.WebSockets.IsWebSocketRequest)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await EchoFramesAsync(socket, context.RequestAborted);
            return;
        }

        if (request.Headers.TryGetValue("x-test-delay-ms", out var delay))
            await Task.Delay(int.Parse(delay.ToString()), context.RequestAborted);

        context.Response.Headers["X-Powered-By"] = "echo";

        if (request.Headers.TryGetValue("x-test-status", out var status))
        {
            context.Response.StatusCode = int.Parse(status.ToString());
            await context.Response.WriteAsync("boom");
            return;
        }

        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, context.RequestAborted);

        await context.Response.WriteAsJsonAsync(new
        {
            method = request.Method,
            path = request.Path.Value,
            query = request.QueryString.Value,
            headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()),
            bodyLength = body.Length,
        });
    }

    private static async Task EchoFramesAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var frame = await socket.ReceiveAsync(buffer, ct);
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return;
            }
            await socket.SendAsync(buffer.AsMemory(0, frame.Count), frame.MessageType, frame.EndOfMessage, ct);
        }
    }
}
