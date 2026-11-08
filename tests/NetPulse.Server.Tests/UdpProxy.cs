using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NetPulse.Server.Tests;

public enum Direction
{
    ClientToServer,
    ServerToClient
}

public sealed class UdpProxy : IDisposable
{
    private readonly UdpClient _front = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly IPEndPoint _server;
    private readonly ConcurrentDictionary<IPEndPoint, UdpClient> _upstreams = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<IPEndPoint> _order = new();

    public UdpProxy(int serverPort)
    {
        _server = new IPEndPoint(IPAddress.Loopback, serverPort);
        _ = Task.Run(() => FrontLoopAsync(_cts.Token));
    }

    public int Port => ((IPEndPoint)_front.Client.LocalEndPoint!).Port;

    public ConcurrentQueue<(Direction Direction, byte[] Data)> Capture { get; } = new();

    public Func<Direction, byte[], IEnumerable<byte[]>> Filter { get; set; } = (_, data) => new[] { data };

    public IReadOnlyList<byte[]> Datagrams(Direction direction) =>
        Capture.Where(c => c.Direction == direction).Select(c => c.Data).ToArray();

    public IPEndPoint? FirstClient => _order.TryPeek(out var first) ? first : null;

    public IReadOnlyList<IPEndPoint> Clients => _order.ToArray();

    public IPEndPoint? UpstreamEndpointOf(IPEndPoint client) =>
        _upstreams.TryGetValue(client, out var upstream) ? (IPEndPoint)upstream.Client.LocalEndPoint! : null;

    public async Task SendToServerAsSameEndpointAsync(IPEndPoint client, byte[] datagram) =>
        await _upstreams[client].SendAsync(datagram, datagram.Length, _server);

    public async Task<byte[]?> ReceiveFromServerOnSameEndpointAsync(IPEndPoint client, TimeSpan timeout)
    {
        try
        {
            var result = await _upstreams[client].ReceiveAsync(_cts.Token).AsTask().WaitAsync(timeout);
            return result.Buffer;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _front.Dispose();

        foreach (var upstream in _upstreams.Values)
        {
            upstream.Dispose();
        }
    }

    private async Task FrontLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received = await _front.ReceiveAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var upstream = _upstreams.GetOrAdd(received.RemoteEndPoint, client =>
            {
                var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                _order.Enqueue(client);
                _ = Task.Run(() => UpstreamLoopAsync(client, socket, token));
                return socket;
            });

            Capture.Enqueue((Direction.ClientToServer, received.Buffer));

            foreach (var datagram in Filter(Direction.ClientToServer, received.Buffer))
            {
                await upstream.SendAsync(datagram, datagram.Length, _server);
            }
        }
    }

    private async Task UpstreamLoopAsync(IPEndPoint client, UdpClient upstream, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received = await upstream.ReceiveAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            Capture.Enqueue((Direction.ServerToClient, received.Buffer));

            foreach (var datagram in Filter(Direction.ServerToClient, received.Buffer))
            {
                await _front.SendAsync(datagram, datagram.Length, client);
            }
        }
    }
}
