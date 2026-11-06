using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Configuration;
using NetPulse.Server.Core;
using NetPulse.Server.Networking;
using NetPulse.Server.Plugins;

namespace NetPulse.Server.Rooms;

public sealed record RoomSnapshot(RoomInfo Info, bool IsVisible);

public sealed record RoomRequestContext(Peer Peer, byte OpCode, ushort RequestId);

public readonly record struct JoinOutcome(short Code, string? Message)
{
    public static JoinOutcome Ok { get; } = new(ReturnCode.Ok, null);
}

public interface IRoomDirectory
{
    void RoomUpdated(Room room, RoomSnapshot snapshot);

    void RoomClosed(Room room);
}

public sealed class RoomServices
{
    public RoomServices(
        ILoggerFactory loggerFactory,
        ServerClock clock,
        ServerMetrics metrics,
        PluginManager plugins,
        IHttpClientFactory httpFactory,
        IOptions<NetPulseOptions> options)
    {
        LoggerFactory = loggerFactory;
        Clock = clock;
        Metrics = metrics;
        Plugins = plugins;
        HttpFactory = httpFactory;
        QueueCapacity = Math.Max(1, options.Value.RoomQueueCapacity);
        TickRate = Math.Max(1, options.Value.TickRate);
        OwnershipRequestTimeoutMs = Math.Max(1, options.Value.OwnershipRequestTimeoutMs);
        Flood = new FloodGuard(loggerFactory.CreateLogger("NetPulse.Flood"), metrics, options.Value.PeerQueueLimit);
    }

    public ILoggerFactory LoggerFactory { get; }

    public ServerClock Clock { get; }

    public ServerMetrics Metrics { get; }

    public PluginManager Plugins { get; }

    public IHttpClientFactory HttpFactory { get; }

    public int QueueCapacity { get; }

    public int TickRate { get; }

    public int OwnershipRequestTimeoutMs { get; }

    public FloodGuard Flood { get; }
}

public sealed class CachedEvent
{
    public byte Code { get; init; }

    public object? Data { get; init; }

    public int Sender { get; init; }

    public int ViewId { get; init; }

    public byte Group { get; init; }

    public bool IsGlobal { get; init; }
}

public sealed class PassThroughPlugin : RoomPlugin
{
    public PassThroughPlugin(IPluginHost host)
        : base(host)
    {
    }

    public override string Name => "PassThrough";
}
