using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Bloom.Shared.Tests;

/// <summary>TCP forwarder to the broker. While <see cref="DropConfirms"/> is set, the broker's publisher confirms (basic.ack and basic.nack frames) never reach the client; everything else passes.</summary>
public sealed class DelayedConfirmProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public volatile bool DropConfirms;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public DelayedConfirmProxy(string upstreamHost, int upstreamPort)
    {
        _listener.Start();
        _accepting = AcceptAsync(upstreamHost, upstreamPort);
    }

    private async Task AcceptAsync(string host, int port)
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var server = new TcpClient();
                await server.ConnectAsync(host, port, _stop.Token);
                _ = Pump(client, server, fromBroker: false);
                _ = Pump(server, client, fromBroker: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Pump(TcpClient from, TcpClient to, bool fromBroker)
    {
        try
        {
            var input = from.GetStream();
            var output = to.GetStream();
            var buffer = new byte[16 * 1024];
            int n;
            while (!fromBroker && (n = await input.ReadAsync(buffer, _stop.Token)) > 0)
                await output.WriteAsync(buffer.AsMemory(0, n), _stop.Token);
            while (fromBroker) // AMQP frames: type (1), channel (2), size (4), payload, end (1)
            {
                var frame = new byte[7];
                await input.ReadExactlyAsync(frame, _stop.Token);
                Array.Resize(ref frame, 8 + (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(3)));
                await input.ReadExactlyAsync(frame.AsMemory(7), _stop.Token);
                var confirm = frame[0] == 1 && frame.Length >= 11 && frame[7] == 0 && frame[8] == 60 && frame[10] is 80 or 120; // basic.ack, basic.nack
                if (!(DropConfirms && confirm)) await output.WriteAsync(frame, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException or ObjectDisposedException)
        {
        }
        finally
        {
            from.Dispose();
            to.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }
}
