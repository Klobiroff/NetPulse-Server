using System.Diagnostics.Metrics;

namespace NetPulse.Server.Core;

public sealed class ServerMetrics : IDisposable
{
    public const string MeterName = "NetPulse";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _messagesIn;
    private readonly Counter<long> _messagesOut;
    private long _peers;
    private long _rooms;
    private long _messagesInTotal;
    private long _messagesOutTotal;
    private long _bytesInTotal;
    private long _bytesOutTotal;
    private long _pluginErrors;
    private long _peersOverloaded;
    private long _ownershipViolations;

    public ServerMetrics()
    {
        _messagesIn = _meter.CreateCounter<long>("netpulse.msgs_in");
        _messagesOut = _meter.CreateCounter<long>("netpulse.msgs_out");
        _meter.CreateObservableGauge("netpulse.peers", () => Interlocked.Read(ref _peers));
        _meter.CreateObservableGauge("netpulse.rooms", () => Interlocked.Read(ref _rooms));
        _meter.CreateObservableCounter("netpulse.peers_overloaded", () => Interlocked.Read(ref _peersOverloaded));
        _meter.CreateObservableCounter("netpulse.ownership_violations", () => Interlocked.Read(ref _ownershipViolations));
    }

    public long Peers => Interlocked.Read(ref _peers);

    public long Rooms => Interlocked.Read(ref _rooms);

    public long MessagesIn => Interlocked.Read(ref _messagesInTotal);

    public long MessagesOut => Interlocked.Read(ref _messagesOutTotal);

    public long BytesIn => Interlocked.Read(ref _bytesInTotal);

    public long BytesOut => Interlocked.Read(ref _bytesOutTotal);

    public long PluginErrors => Interlocked.Read(ref _pluginErrors);

    public long PeersOverloaded => Interlocked.Read(ref _peersOverloaded);

    public long OwnershipViolations => Interlocked.Read(ref _ownershipViolations);

    public void PeerConnected() => Interlocked.Increment(ref _peers);

    public void PeerDisconnected() => Interlocked.Decrement(ref _peers);

    public void RoomCreated() => Interlocked.Increment(ref _rooms);

    public void RoomClosed() => Interlocked.Decrement(ref _rooms);

    public void PluginError() => Interlocked.Increment(ref _pluginErrors);

    public void PeerOverloaded() => Interlocked.Increment(ref _peersOverloaded);

    public void OwnershipViolation() => Interlocked.Increment(ref _ownershipViolations);

    public void RecordIn(int bytes)
    {
        Interlocked.Increment(ref _messagesInTotal);
        Interlocked.Add(ref _bytesInTotal, bytes);
        _messagesIn.Add(1);
    }

    public void RecordOut(int bytes)
    {
        Interlocked.Increment(ref _messagesOutTotal);
        Interlocked.Add(ref _bytesOutTotal, bytes);
        _messagesOut.Add(1);
    }

    public void Dispose() => _meter.Dispose();
}
