using LiteNetLib;
using NetPulse.Common;

namespace NetPulse.TestClient;

public sealed class LiteNetTransport : INetTransport, IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private NetPeer? _peer;

    public LiteNetTransport()
    {
        _manager = new NetManager(_listener) { AutoRecycle = true };
        _listener.PeerConnectedEvent += peer =>
        {
            _peer = peer;
            Connected?.Invoke();
        };
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            _peer = null;
            var extra = info.AdditionalData.AvailableBytes > 0 ? System.Text.Encoding.UTF8.GetString(info.AdditionalData.GetRemainingBytes()) : string.Empty;
            Disconnected?.Invoke($"{info.Reason}:{extra}");
        };
        _listener.NetworkReceiveEvent += (_, reader, _, method) =>
            DataReceived?.Invoke(reader.GetRemainingBytesSegment(), FromMethod(method));
    }

    public event Action? Connected;

    public event Action<string>? Disconnected;

    public event Action<ArraySegment<byte>, DeliveryMode>? DataReceived;

    public bool IsConnected => _peer is { ConnectionState: ConnectionState.Connected };

    public int RoundTripTimeMs => _peer?.Ping * 2 ?? 0;

    public void Connect(string host, int port, string connectionKey)
    {
        _manager.Start();
        _manager.Connect(host, port, connectionKey);
    }

    public void Send(ArraySegment<byte> data, DeliveryMode mode) =>
        _peer?.Send(data.Array!, data.Offset, data.Count, ToMethod(mode));

    public void Poll() => _manager.PollEvents();

    public void Disconnect() => _manager.Stop();

    public void Dispose() => _manager.Stop();

    private static DeliveryMethod ToMethod(DeliveryMode mode) => mode switch
    {
        DeliveryMode.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        DeliveryMode.ReliableUnordered => DeliveryMethod.ReliableUnordered,
        DeliveryMode.UnreliableSequenced => DeliveryMethod.Sequenced,
        _ => DeliveryMethod.Unreliable
    };

    private static DeliveryMode FromMethod(DeliveryMethod method) => method switch
    {
        DeliveryMethod.ReliableOrdered => DeliveryMode.ReliableOrdered,
        DeliveryMethod.ReliableUnordered => DeliveryMode.ReliableUnordered,
        DeliveryMethod.Sequenced => DeliveryMode.UnreliableSequenced,
        _ => DeliveryMode.Unreliable
    };
}
