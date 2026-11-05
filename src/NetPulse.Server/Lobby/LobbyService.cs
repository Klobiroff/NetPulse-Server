using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetPulse.Common;
using NetPulse.Server.Configuration;
using NetPulse.Server.Core;
using NetPulse.Server.Networking;
using NetPulse.Server.Rooms;

namespace NetPulse.Server.Lobby;

public sealed class LobbyService : IRoomDirectory, IAsyncDisposable
{
    private readonly RoomServices _services;
    private readonly ILogger _logger;
    private readonly Fiber _fiber;
    private readonly Dictionary<string, RoomEntry> _rooms = new(StringComparer.Ordinal);
    private readonly HashSet<Peer> _subscribers = new();
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly Timer _flushTimer;

    public LobbyService(RoomServices services, IOptions<NetPulseOptions> options, ILoggerFactory loggerFactory)
    {
        _services = services;
        _logger = loggerFactory.CreateLogger("NetPulse.Lobby");
        _fiber = new Fiber("lobby", _logger, Math.Max(1, options.Value.RoomQueueCapacity));
        var interval = Math.Max(50, options.Value.RoomListIntervalMs);
        _flushTimer = new Timer(_ => _fiber.Post(FlushDeltas), null, interval, interval);
    }

    public void Handle(Peer peer, OperationRequest request)
    {
        if (_services.Flood.TryPost(_fiber, peer, () => DispatchAsync(peer, request), "lobby") == PostResult.Closed)
        {
            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.InternalError, "Lobby unavailable");
        }
    }

    public void PeerDisconnected(Peer peer) => _fiber.Post(() => _subscribers.Remove(peer));

    public void RoomUpdated(Room room, RoomSnapshot snapshot) => _fiber.Post(() =>
    {
        if (!_rooms.TryGetValue(room.Name, out var entry) || !ReferenceEquals(entry.Room, room))
        {
            return;
        }

        entry.Snapshot = snapshot;
        _dirty.Add(room.Name);
    });

    public void RoomClosed(Room room) => _fiber.Post(() =>
    {
        if (_rooms.TryGetValue(room.Name, out var entry) && ReferenceEquals(entry.Room, room))
        {
            _rooms.Remove(room.Name);
            _dirty.Remove(room.Name);

            if (entry.Snapshot.IsVisible)
            {
                _removed.Add(room.Name);
            }
        }
    });

    public async Task<IReadOnlyList<RoomInfo>> SnapshotRoomsAsync() =>
        await _fiber.InvokeAsync<IReadOnlyList<RoomInfo>>(() => ValueTask.FromResult<IReadOnlyList<RoomInfo>>(_rooms.Values.Select(e => e.Snapshot.Info).ToArray()));

    public async Task ShutdownRoomsAsync(TimeSpan timeout)
    {
        var rooms = await _fiber.InvokeAsync(() => ValueTask.FromResult(_rooms.Values.Select(e => e.Room).ToArray()));
        var shutdown = Task.WhenAll(rooms.Select(r => r.ShutdownAsync()));

        try
        {
            await shutdown.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Not all rooms closed within {Timeout}", timeout);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _flushTimer.DisposeAsync();
        await _fiber.DisposeAsync();
    }

    private ValueTask DispatchAsync(Peer peer, OperationRequest request)
    {
        try
        {
            switch (request.OpCode)
            {
                case OpCode.JoinLobby:
                    _subscribers.Add(peer);
                    peer.InLobby = true;
                    peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.Ok);
                    SendRoomList(peer);
                    break;
                case OpCode.LeaveLobby:
                    _subscribers.Remove(peer);
                    peer.InLobby = false;
                    peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.Ok);
                    break;
                case OpCode.GetRoomList:
                    peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.Ok, null, BuildFullList().ToParameters());
                    break;
                case OpCode.CreateRoom:
                    StartJoin(peer, request, context => HandleCreateAsync(context, CreateRoomRequest.FromParameters(request.Parameters)));
                    break;
                case OpCode.JoinRoom:
                    StartJoin(peer, request, context => HandleJoinAsync(context, JoinRoomRequest.FromParameters(request.Parameters)));
                    break;
                case OpCode.JoinOrCreateRoom:
                    StartJoin(peer, request, context => HandleJoinOrCreateAsync(context, JoinOrCreateRoomRequest.FromParameters(request.Parameters)));
                    break;
                case OpCode.JoinRandomRoom:
                    StartJoin(peer, request, context => HandleJoinRandomAsync(context, JoinRandomRequest.FromParameters(request.Parameters)));
                    break;
                default:
                    peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.InvalidRequest, "Unknown operation");
                    break;
            }
        }
        catch (Exception ex) when (ex is NetSerializationException or InvalidCastException or ArgumentException)
        {
            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.InvalidRequest, "Malformed request");
        }

        return ValueTask.CompletedTask;
    }

    private void StartJoin(Peer peer, OperationRequest request, Func<RoomRequestContext, Task<JoinOutcome>> attempt)
    {
        if (peer.Room is not null)
        {
            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.AlreadyInRoom, "Already in a room");
            return;
        }

        if (peer.JoinInFlight)
        {
            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.InvalidRequest, "Another join is in progress");
            return;
        }

        peer.JoinInFlight = true;
        _ = RunJoinAsync(new RoomRequestContext(peer, request.OpCode, request.RequestId), attempt);
    }

    private async Task RunJoinAsync(RoomRequestContext context, Func<RoomRequestContext, Task<JoinOutcome>> attempt)
    {
        JoinOutcome outcome;

        try
        {
            outcome = await attempt(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Join failed unexpectedly");
            outcome = new JoinOutcome(ReturnCode.InternalError, "Internal error");
        }

        context.Peer.JoinInFlight = false;

        if (outcome.Code != ReturnCode.Ok)
        {
            context.Peer.SendResponse(context.OpCode, context.RequestId, outcome.Code, outcome.Message);
        }
    }

    private Task<JoinOutcome> HandleCreateAsync(RoomRequestContext context, CreateRoomRequest data)
    {
        var name = string.IsNullOrWhiteSpace(data.RoomName) ? NewRoomName() : data.RoomName;

        if (_rooms.ContainsKey(name))
        {
            return Task.FromResult(new JoinOutcome(ReturnCode.RoomAlreadyExists, "Room already exists"));
        }

        return CreateRoomAsync(context, name, data.Options, data.PlayerProperties);
    }

    private Task<JoinOutcome> HandleJoinAsync(RoomRequestContext context, JoinRoomRequest data)
    {
        if (data.RoomName is null || !_rooms.TryGetValue(data.RoomName, out var entry))
        {
            return Task.FromResult(new JoinOutcome(ReturnCode.RoomNotFound, "Room not found"));
        }

        return entry.Room.JoinAsync(context, data.PlayerProperties);
    }

    private Task<JoinOutcome> HandleJoinOrCreateAsync(RoomRequestContext context, JoinOrCreateRoomRequest data)
    {
        var name = string.IsNullOrWhiteSpace(data.RoomName) ? NewRoomName() : data.RoomName;

        return _rooms.TryGetValue(name, out var entry)
            ? entry.Room.JoinAsync(context, data.PlayerProperties)
            : CreateRoomAsync(context, name, data.Options, data.PlayerProperties);
    }

    private async Task<JoinOutcome> HandleJoinRandomAsync(RoomRequestContext context, JoinRandomRequest data)
    {
        var candidates = _rooms.Values
            .Where(e => e.Snapshot.IsVisible && e.Snapshot.Info.IsOpen && !IsFull(e.Snapshot.Info))
            .Where(e => data.ExpectedMaxPlayers == 0 || e.Snapshot.Info.MaxPlayers == data.ExpectedMaxPlayers)
            .Where(e => e.Snapshot.Info.LobbyProperties.Matches(data.ExpectedProperties))
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        foreach (var candidate in candidates)
        {
            var outcome = await candidate.Room.JoinAsync(context, data.PlayerProperties);

            if (outcome.Code is not (ReturnCode.RoomFull or ReturnCode.RoomClosed or ReturnCode.RoomNotFound))
            {
                return outcome;
            }
        }

        if (data.CreateOptions is null)
        {
            return new JoinOutcome(ReturnCode.NoMatchFound, "No matching room");
        }

        return await CreateRoomAsync(context, NewRoomName(), data.CreateOptions, data.PlayerProperties);
    }

    private Task<JoinOutcome> CreateRoomAsync(RoomRequestContext context, string name, RoomOptions? requested, PropertyTable? playerProperties)
    {
        var room = new Room(name, requested ?? new RoomOptions(), _services, this);
        _rooms[name] = new RoomEntry(room, room.InitialSnapshot());
        _removed.Remove(name);
        _logger.LogInformation("Creating room {Room} for {User}", name, context.Peer.UserId);
        return room.CreateAsync(context, playerProperties);
    }

    private static bool IsFull(RoomInfo info) => info.MaxPlayers > 0 && info.PlayerCount >= info.MaxPlayers;

    private static string NewRoomName() => Guid.NewGuid().ToString("N")[..12];

    private RoomListData BuildFullList() => new()
    {
        IsFullList = true,
        Rooms = _rooms.Values.Where(e => e.Snapshot.IsVisible).Select(e => e.Snapshot.Info).ToArray()
    };

    private void SendRoomList(Peer peer) => peer.SendEvent(EventCode.RoomList, BuildFullList().ToParameters());

    private void FlushDeltas()
    {
        if (_dirty.Count == 0 && _removed.Count == 0)
        {
            return;
        }

        var updated = new List<RoomInfo>();
        var removed = new List<string>(_removed);

        foreach (var name in _dirty)
        {
            if (!_rooms.TryGetValue(name, out var entry))
            {
                continue;
            }

            if (entry.Snapshot.IsVisible)
            {
                updated.Add(entry.Snapshot.Info);
            }
            else
            {
                removed.Add(name);
            }
        }

        _dirty.Clear();
        _removed.Clear();

        if (updated.Count == 0 && removed.Count == 0)
        {
            return;
        }

        var delta = new RoomListData { IsFullList = false, Rooms = updated.ToArray(), RemovedRooms = removed.ToArray() };
        var packet = Wire.Event(EventCode.RoomList, delta.ToParameters());

        foreach (var peer in _subscribers)
        {
            peer.Send(packet);
        }
    }

    private sealed class RoomEntry
    {
        public RoomEntry(Room room, RoomSnapshot snapshot)
        {
            Room = room;
            Snapshot = snapshot;
        }

        public Room Room { get; }

        public RoomSnapshot Snapshot { get; set; }
    }
}
