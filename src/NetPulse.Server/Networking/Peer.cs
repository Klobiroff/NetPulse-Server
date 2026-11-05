using LiteNetLib;
using NetPulse.Common;
using NetPulse.Server.Core;
using NetPulse.Server.Rooms;

namespace NetPulse.Server.Networking;

public sealed class Peer
{
    private readonly NetPeer? _net;
    private readonly ServerMetrics _metrics;
    private int _inFlight;
    private int _overloaded;

    public Peer(int id, NetPeer? net, string endpoint, ServerMetrics metrics)
    {
        Id = id;
        _net = net;
        Endpoint = endpoint;
        _metrics = metrics;
        Connected = true;
    }

    public int Id { get; }

    public string Endpoint { get; }

    public volatile bool Connected;

    public volatile bool Authenticated;

    public volatile bool InLobby;

    public volatile bool JoinInFlight;

    public string UserId { get; set; } = string.Empty;

    public string NickName { get; set; } = string.Empty;

    public Room? Room { get; set; }

    public int InFlight => Volatile.Read(ref _inFlight);

    public bool TryAcquireSlot(int limit)
    {
        if (Interlocked.Increment(ref _inFlight) <= limit)
        {
            return true;
        }

        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    public void ReleaseSlot() => Interlocked.Decrement(ref _inFlight);

    public bool MarkOverloaded() => Interlocked.Exchange(ref _overloaded, 1) == 0;

    public int RoundTripTimeMs => _net?.Ping * 2 ?? 0;

    public void Send(ArraySegment<byte> data, DeliveryMode mode = DeliveryMode.ReliableOrdered)
    {
        if (!Connected || _net is null)
        {
            return;
        }

        _net.Send(data.Array!, data.Offset, data.Count, ToMethod(mode));
        _metrics.RecordOut(data.Count);
    }

    public void SendResponse(byte opCode, ushort requestId, short returnCode, string? message = null, ParameterDictionary? parameters = null) =>
        Send(Wire.Response(opCode, requestId, returnCode, message, parameters));

    public void SendEvent(byte code, ParameterDictionary? parameters, DeliveryMode mode = DeliveryMode.ReliableOrdered) =>
        Send(Wire.Event(code, parameters), mode);

    public void Disconnect(string? reason = null) => _net?.Disconnect(reason is null ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(reason));

    private static DeliveryMethod ToMethod(DeliveryMode mode) => mode switch
    {
        DeliveryMode.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        DeliveryMode.ReliableUnordered => DeliveryMethod.ReliableUnordered,
        DeliveryMode.UnreliableSequenced => DeliveryMethod.Sequenced,
        _ => DeliveryMethod.Unreliable
    };
}
