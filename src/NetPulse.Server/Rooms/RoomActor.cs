using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Networking;

namespace NetPulse.Server.Rooms;

public sealed class RoomActor : IActor
{
    private readonly Room _room;

    public RoomActor(Room room, int actorId, Peer peer, PropertyTable properties)
    {
        _room = room;
        ActorId = actorId;
        Peer = peer;
        UserId = peer.UserId;
        NickName = peer.NickName;
        Properties = properties;
    }

    public int ActorId { get; }

    public string UserId { get; }

    public string NickName { get; }

    public PropertyTable Properties { get; }

    public bool IsInactive { get; set; }

    public string? RejoinToken { get; set; }

    public bool IsEncrypted => Peer?.IsEncrypted ?? false;

    public bool IsMasterClient => _room.MasterClientId == ActorId;

    public Peer? Peer { get; set; }

    public HashSet<byte> Groups { get; } = new();

    public IDisposable? TtlTimer { get; set; }

    public long LastViolationMs { get; set; } = -10000;

    public PlayerInfo ToInfo() => new()
    {
        ActorId = ActorId,
        NickName = NickName,
        UserId = UserId,
        Properties = Properties.Clone(),
        IsInactive = IsInactive
    };
}
