using LiteNetLib;
using NetPulse.Common;
using NetPulse.Common.Security;

namespace NetPulse.TestClient;

public sealed class LiteNetTransport : ISecureNetTransport, IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly SecurityCounters _counters = new();
    private NetManager? _manager;
    private SecureClientCore? _core;
    private SecureClientSettings? _security;
    private NetPeer? _peer;
    private bool _raisedFailure;

    public LiteNetTransport()
    {
        _listener.PeerConnectedEvent += peer =>
        {
            _peer = peer;
            Connected?.Invoke();
        };
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            _peer = null;

            if (_raisedFailure)
            {
                return;
            }

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

    public SecurityCounters Counters => _counters;

    public bool IsEncrypted => _core?.Session is not null;

    public byte[]? HandshakeHash => _core?.HandshakeHash;

    public void ConfigureSecurity(SecureClientSettings? settings) => _security = settings;

    public void Connect(string host, int port, string connectionKey)
    {
        _raisedFailure = false;
        _manager?.Stop();
        _core?.Dispose();
        _core = null;

        if (_security is null)
        {
            _manager = new NetManager(_listener) { AutoRecycle = true };
        }
        else
        {
            _core = new SecureClientCore(_security, _counters, () => MonotonicClock.NowMs);
            var layer = new SecureClientLayer(_core);
            _manager = new NetManager(_listener, layer) { AutoRecycle = true };
            layer.Attach(_manager);
        }

        _manager.Start();
        _manager.Connect(host, port, connectionKey);
    }

    public void Send(ArraySegment<byte> data, DeliveryMode mode) =>
        _peer?.Send(data.Array!, data.Offset, data.Count, ToMethod(mode));

    public void Poll()
    {
        _manager?.PollEvents();

        if (_core is null || _raisedFailure)
        {
            return;
        }

        var reason = _core.State switch
        {
            SecureClientState.Failed => SecureDisconnectReason.SecureHandshakeFailed,
            _ when _core.CheckTimeout() => SecureDisconnectReason.SecureHandshakeTimeout,
            _ when _core.Session is { IsClosed: true } => SecureDisconnectReason.SecureHandshakeFailed,
            _ => null
        };

        if (reason is null)
        {
            return;
        }

        _raisedFailure = true;
        _manager?.Stop();
        Disconnected?.Invoke(reason);
    }

    public void Disconnect() => _manager?.Stop();

    public void Dispose()
    {
        _manager?.Stop();
        _core?.Dispose();
    }

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
