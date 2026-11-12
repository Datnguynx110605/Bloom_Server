using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bloom.Catalog.Tests;

/// <summary>
/// A TCP proxy in front of one Redis endpoint. While <see cref="Stalled"/>, it records what clients send but forwards nothing, so a
/// connected client sees a Redis that accepts commands and never answers (RedisTimeoutException), without touching the shared container.
/// </summary>
public sealed class StallableProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly StringBuilder _received = new();
    private readonly List<TcpClient> _clients = [];

    public StallableProxy(string upstreamHost, int upstreamPort)
    {
        (_upstreamHost, _upstreamPort) = (upstreamHost, upstreamPort);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public volatile bool Stalled;

    /// <summary>Everything clients sent since the last <see cref="Reset"/>, as text (RESP is ASCII for our keys).</summary>
    public string Received
    {
        get { lock (_received) return _received.ToString(); }
    }

    public void Reset()
    {
        lock (_received) _received.Clear();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                var upstream = new TcpClient();
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort);
                lock (_clients) _clients.AddRange([client, upstream]);
                _ = PumpAsync(client, upstream, record: true);
                _ = PumpAsync(upstream, client, record: false);
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException or IOException) { } // disposed
    }

    private async Task PumpAsync(TcpClient from, TcpClient to, bool record)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.GetStream().ReadAsync(buffer);
                if (n == 0) return;
                if (record) lock (_received) _received.Append(Encoding.Latin1.GetString(buffer, 0, n));
                if (!Stalled) await to.GetStream().WriteAsync(buffer.AsMemory(0, n));
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or SocketException or IOException or InvalidOperationException) { } // peer closed
    }

    public void Dispose()
    {
        _listener.Stop();
        lock (_clients) foreach (var c in _clients) c.Dispose();
    }
}
