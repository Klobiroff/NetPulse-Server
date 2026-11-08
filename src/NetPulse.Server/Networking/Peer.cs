using LiteNetLib;
using NetPulse.Common;
using NetPulse.Common.Security;
using NetPulse.Server.Configuration;
using NetPulse.Server.Core;
using NetPulse.Server.Rooms;

namespace NetPulse.Server.Networking;

public enum AdmitResult
{
    Allowed,
    MessageTooLarge,
    TooManyMessages,
    TooManyBytes
}

public sealed class Peer
{
    private readonly NetPeer? _net;
    private readonly ServerMetrics _metrics;
    private readonly object _limitGate = new();
    private int _inFlight;
    private int _overloaded;
    private int _authAttempts;
    private double _messageTokens = -1;
    private double _byteTokens = -1;
    private long _limitMs;

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

    public SecureSession? Session { get; set; }

    public byte[]? ChannelBinding { get; set; }

    public long ConnectedAtMs { get; set; }

    public bool IsEncrypted => Session is not null;

    internal NetPeer? Net => _net;

    public int InFlight => Volatile.Read(ref _inFlight);

    public int NextAuthAttempt() => Interlocked.Increment(ref _authAttempts);

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

    public AdmitResult Admit(int bytes, SecurityOptions limits, long nowMs)
    {
        if (limits.MaxMessageBytes > 0 && bytes > limits.MaxMessageBytes)
        {
            return AdmitResult.MessageTooLarge;
        }

        if (limits.MaxMessagesPerSecond <= 0 && limits.MaxBytesPerSecond <= 0)
        {
            return AdmitResult.Allowed;
        }

        lock (_limitGate)
        {
            var messageBurst = Math.Max(limits.MessageBurst, limits.MaxMessagesPerSecond);
            var byteBurst = Math.Max(limits.ByteBurst, limits.MaxBytesPerSecond);

            if (_messageTokens < 0)
            {
                _messageTokens = messageBurst;
                _byteTokens = byteBurst;
                _limitMs = nowMs;
            }

            var elapsed = Math.Max(0, nowMs - _limitMs);
            _limitMs = nowMs;
            _messageTokens = Math.Min(messageBurst, _messageTokens + (elapsed * limits.MaxMessagesPerSecond / 1000.0));
            _byteTokens = Math.Min(byteBurst, _byteTokens + (elapsed * limits.MaxBytesPerSecond / 1000.0));

            if (limits.MaxMessagesPerSecond > 0)
            {
                _messageTokens -= 1;

                if (_messageTokens < 0)
                {
                    _messageTokens = 0;
                    return AdmitResult.TooManyMessages;
                }
            }

            if (limits.MaxBytesPerSecond > 0)
            {
                _byteTokens -= bytes;

                if (_byteTokens < 0)
                {
                    _byteTokens = 0;
                    return AdmitResult.TooManyBytes;
                }
            }

            return AdmitResult.Allowed;
        }
    }

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
