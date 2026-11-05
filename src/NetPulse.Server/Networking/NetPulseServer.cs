using System.Collections.Concurrent;
using LiteNetLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetPulse.Common;
using NetPulse.Server.Configuration;
using NetPulse.Server.Core;
using NetPulse.Server.Lobby;
using NetPulse.Server.Plugins;

namespace NetPulse.Server.Networking;

public sealed class NetPulseServer : IHostedService
{
    private readonly NetPulseOptions _options;
    private readonly ILogger<NetPulseServer> _logger;
    private readonly ServerClock _clock;
    private readonly ServerMetrics _metrics;
    private readonly LobbyService _lobby;
    private readonly Authenticator _authenticator;
    private readonly PluginManager _plugins;
    private readonly ConcurrentDictionary<int, Peer> _peers = new();
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private volatile bool _stopping;

    public NetPulseServer(
        IOptions<NetPulseOptions> options,
        ILogger<NetPulseServer> logger,
        ServerClock clock,
        ServerMetrics metrics,
        LobbyService lobby,
        Authenticator authenticator,
        PluginManager plugins)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock;
        _metrics = metrics;
        _lobby = lobby;
        _authenticator = authenticator;
        _plugins = plugins;
        _manager = new NetManager(_listener)
        {
            AutoRecycle = true,
            UnsyncedEvents = true,
            DisconnectTimeout = _options.DisconnectTimeoutMs,
            UpdateTime = Math.Max(1, 1000 / Math.Max(1, _options.TickRate))
        };
    }

    public int Port => _manager.LocalPort;

    public int PeerCount => _peers.Count;

    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToArray();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _plugins.Start();
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (endpoint, error) => _logger.LogWarning("Network error from {Endpoint}: {Error}", endpoint, error);

        if (!_manager.Start(_options.Port))
        {
            throw new InvalidOperationException($"Could not bind UDP port {_options.Port}");
        }

        _logger.LogInformation("NetPulse server listening on UDP {Port}", _manager.LocalPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _logger.LogInformation("NetPulse server shutting down");
        await _lobby.ShutdownRoomsAsync(TimeSpan.FromMilliseconds(_options.ShutdownTimeoutMs));
        _manager.Stop();
        _plugins.Dispose();
        _logger.LogInformation("NetPulse server stopped");
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (_stopping || _peers.Count >= _options.MaxConnections)
        {
            request.Reject();
            return;
        }

        request.AcceptIfKey(_options.ConnectionKey);
    }

    private void OnPeerConnected(NetPeer net)
    {
        var peer = new Peer(net.Id, net, net.ToString(), _metrics);
        net.Tag = peer;
        _peers[net.Id] = peer;
        _metrics.PeerConnected();
        _logger.LogDebug("Peer {Peer} connected from {Endpoint}", peer.Id, peer.Endpoint);
    }

    private void OnPeerDisconnected(NetPeer net, DisconnectInfo info)
    {
        if (net.Tag is not Peer peer)
        {
            return;
        }

        peer.Connected = false;
        _peers.TryRemove(net.Id, out _);
        _metrics.PeerDisconnected();
        _lobby.PeerDisconnected(peer);
        peer.Room?.PeerDisconnected(peer);
        _logger.LogDebug("Peer {Peer} disconnected: {Reason}", peer.Id, info.Reason);
    }

    private void OnReceive(NetPeer net, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (net.Tag is not Peer peer)
        {
            return;
        }

        var segment = reader.GetRemainingBytesSegment();
        _metrics.RecordIn(segment.Count);

        try
        {
            Route(peer, new NetReader(segment));
        }
        catch (NetSerializationException ex)
        {
            _logger.LogWarning("Malformed packet from peer {Peer}: {Message}", peer.Id, ex.Message);
        }
    }

    private void Route(Peer peer, NetReader reader)
    {
        var kind = Protocol.ReadKind(reader);

        switch (kind)
        {
            case MessageKind.Ping:
                Protocol.ReadPingPong(reader, out var clientTicks, out _);
                peer.Send(Wire.Pong(clientTicks, _clock.NowMs), DeliveryMode.Unreliable);
                break;
            case MessageKind.OperationRequest:
                Dispatch(peer, Protocol.ReadOperationRequest(reader));
                break;
            default:
                _logger.LogDebug("Ignoring unexpected message kind {Kind} from peer {Peer}", kind, peer.Id);
                break;
        }
    }

    private void Dispatch(Peer peer, OperationRequest request)
    {
        if (request.OpCode == OpCode.Authenticate)
        {
            _authenticator.Handle(peer, request);
            return;
        }

        if (!peer.Authenticated)
        {
            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.NotAuthenticated, "Authenticate first");
            return;
        }

        switch (request.OpCode)
        {
            case OpCode.JoinLobby:
            case OpCode.LeaveLobby:
            case OpCode.GetRoomList:
            case OpCode.CreateRoom:
            case OpCode.JoinRoom:
            case OpCode.JoinOrCreateRoom:
            case OpCode.JoinRandomRoom:
                _lobby.Handle(peer, request);
                break;
            case OpCode.LeaveRoom:
            case OpCode.RaiseEvent:
            case OpCode.SetRoomProperties:
            case OpCode.SetPlayerProperties:
            case OpCode.ChangeInterestGroups:
            case OpCode.TransferOwnership:
            case OpCode.RequestOwnership:
            case OpCode.RegisterSceneObjects:
                RouteToRoom(peer, request);
                break;
            default:
                peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.InvalidRequest, "Unknown operation");
                break;
        }
    }

    private static void RouteToRoom(Peer peer, OperationRequest request)
    {
        var room = peer.Room;

        if (room is null)
        {
            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.NotInRoom, "Not in a room");
            return;
        }

        room.HandleOperation(peer, request);
    }
}
