using System.Collections.Concurrent;
using LiteNetLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetPulse.Common;
using NetPulse.Common.Security;
using NetPulse.Server.Configuration;
using NetPulse.Server.Core;
using NetPulse.Server.Lobby;
using NetPulse.Server.Plugins;
using NetPulse.Server.Security;

namespace NetPulse.Server.Networking;

public sealed class NetPulseServer : IHostedService
{
    private readonly NetPulseOptions _options;
    private readonly SecurityOptions _limits;
    private readonly ILogger<NetPulseServer> _logger;
    private readonly ServerClock _clock;
    private readonly ServerMetrics _metrics;
    private readonly LobbyService _lobby;
    private readonly Authenticator _authenticator;
    private readonly PluginManager _plugins;
    private readonly ConcurrentDictionary<int, Peer> _peers = new();
    private readonly ConcurrentDictionary<SecureEndpoint, int> _peersPerAddress = new();
    private readonly KeyedRateLimiter<SecureEndpoint> _connectionAttempts;
    private readonly EventBasedNetListener _listener = new();
    private readonly SecureServerCore? _secure;
    private readonly NetManager _manager;
    private readonly CancellationTokenSource _maintenance = new();
    private Task _maintenanceTask = Task.CompletedTask;
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
        _limits = _options.Security;
        _logger = logger;
        _clock = clock;
        _metrics = metrics;
        _lobby = lobby;
        _authenticator = authenticator;
        _plugins = plugins;
        _connectionAttempts = new KeyedRateLimiter<SecureEndpoint>(_limits.ConnectionAttemptsPerIpPerSecond, _limits.ConnectionAttemptsPerIpPerSecond * 2, 100000);

        var settings = EncryptionSettingsFactory.Create(_options, AppContext.BaseDirectory, Environment.GetEnvironmentVariable);
        SecureTransportLayer? layer = null;

        if (settings is not null)
        {
            _secure = new SecureServerCore(settings, metrics.Security, () => clock.NowMs);
            _secure.PeerEvicted += OnPeerEvicted;
            layer = new SecureTransportLayer(_secure);
        }

        _manager = new NetManager(_listener, layer)
        {
            AutoRecycle = true,
            UnsyncedEvents = true,
            DisconnectTimeout = _options.DisconnectTimeoutMs,
            UpdateTime = Math.Max(1, 1000 / Math.Max(1, _options.TickRate))
        };

        layer?.Attach(_manager);
    }

    public int Port => _manager.LocalPort;

    public int PeerCount => _peers.Count;

    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToArray();

    public bool EncryptionEnabled => _secure is not null;

    public SecureServerCore? Secure => _secure;

    public byte[]? PublicKey => _secure?.CurrentPublicKey;

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

        if (_secure is not null)
        {
            _logger.LogInformation("Transport encryption: {Mode}, server public key {PublicKey}", _options.Encryption.Mode, SecurityKeyText.ToText(_secure.CurrentPublicKey));
        }
        else
        {
            _logger.LogInformation("Transport encryption is disabled; traffic is sent in plaintext");
        }

        _maintenanceTask = Task.Run(() => MaintenanceLoopAsync(_maintenance.Token));
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
        _maintenance.Cancel();

        try
        {
            await _maintenanceTask;
        }
        catch (OperationCanceledException)
        {
        }

        await _lobby.ShutdownRoomsAsync(TimeSpan.FromMilliseconds(_options.ShutdownTimeoutMs));
        _manager.Stop();
        _plugins.Dispose();
        _logger.LogInformation("NetPulse server stopped");
    }

    private static SecureEndpoint AddressOf(System.Net.IPEndPoint endpoint) => SecureEndpoint.From(endpoint.Address, 0);

    private async Task MaintenanceLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                Maintain();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Maintain()
    {
        try
        {
            _secure?.Sweep();
            _connectionAttempts.Sweep(_clock.NowMs);

            if (_limits.AuthDeadlineMs <= 0)
            {
                return;
            }

            var now = _clock.NowMs;

            foreach (var peer in _peers.Values)
            {
                if (!peer.Authenticated && peer.Connected && now - peer.ConnectedAtMs > _limits.AuthDeadlineMs)
                {
                    _metrics.AuthTimedOut();
                    _logger.LogInformation("Disconnecting peer {Peer}: no authentication within {Deadline} ms", peer.Id, _limits.AuthDeadlineMs);
                    peer.Disconnect("AuthTimeout");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Server maintenance failed");
        }
    }

    private void OnPeerEvicted(object tag)
    {
        if (tag is Peer { Net: { } net })
        {
            _manager.DisconnectPeerForce(net);
        }
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (_stopping || _peers.Count >= _options.MaxConnections)
        {
            _metrics.ConnectionRejected();
            request.Reject();
            return;
        }

        var address = AddressOf(request.RemoteEndPoint);

        if (!_connectionAttempts.TryAcquire(address, _clock.NowMs))
        {
            _metrics.ConnectionRejected();
            request.Reject();
            return;
        }

        if (_limits.MaxConnectionsPerIp > 0 && _peersPerAddress.TryGetValue(address, out var existing) && existing >= _limits.MaxConnectionsPerIp)
        {
            _metrics.ConnectionRejected();
            request.Reject();
            return;
        }

        request.AcceptIfKey(_options.ConnectionKey);
    }

    private void OnPeerConnected(NetPeer net)
    {
        var peer = new Peer(net.Id, net, net.ToString(), _metrics)
        {
            ConnectedAtMs = _clock.NowMs
        };

        net.Tag = peer;

        if (_secure is not null)
        {
            var key = SecureEndpoint.From(net.Address, net.Port);
            var session = _secure.Bind(key, peer);

            if (session is null && !_secure.AllowPlaintext)
            {
                _logger.LogWarning("Peer {Peer} connected without a secure session; disconnecting", peer.Id);
                net.Disconnect();
                return;
            }

            if (session is not null)
            {
                peer.Session = session;
                peer.ChannelBinding = session.HandshakeHash;
            }
        }

        _peers[net.Id] = peer;
        _peersPerAddress.AddOrUpdate(AddressOf(net), 1, (_, count) => count + 1);
        _metrics.PeerConnected();
        _logger.LogDebug("Peer {Peer} connected from {Endpoint} (encrypted: {Encrypted})", peer.Id, peer.Endpoint, peer.IsEncrypted);
    }

    private void OnPeerDisconnected(NetPeer net, DisconnectInfo info)
    {
        if (net.Tag is not Peer peer)
        {
            return;
        }

        net.Tag = null;
        peer.Connected = false;

        if (peer.Session is { } session)
        {
            _secure?.Release(session);
        }

        if (_peers.TryRemove(net.Id, out _))
        {
            var address = AddressOf(net);
            var remaining = _peersPerAddress.AddOrUpdate(address, 0, (_, count) => Math.Max(0, count - 1));

            if (remaining == 0)
            {
                _peersPerAddress.TryRemove(new KeyValuePair<SecureEndpoint, int>(address, 0));
            }

            _metrics.PeerDisconnected();
        }

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

        var admit = peer.Admit(segment.Count, _limits, _clock.NowMs);

        if (admit != AdmitResult.Allowed)
        {
            _metrics.PeerRateLimited();
            _logger.LogWarning("Disconnecting peer {Peer}: {Violation}", peer.Id, admit);
            peer.Disconnect(SecureDisconnectReason.RateLimited);
            return;
        }

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
