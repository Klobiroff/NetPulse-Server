using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Core;
using NetPulse.Server.Networking;
using NetPulse.Server.Plugins;

namespace NetPulse.Server.Rooms;

public sealed partial class Room
{
    private readonly RoomOptions _options;
    private readonly RoomServices _services;
    private readonly IRoomDirectory _directory;
    private readonly ILogger _logger;
    private readonly Fiber _fiber;
    private readonly PropertyTable _properties;
    private string? _currentScene;
    private readonly List<RoomActor> _actors = new();
    private readonly List<CachedEvent> _cache = new();
    private readonly PluginHost _host;
    private readonly TimeSpan _hookTimeout;
    private IRoomPlugin _plugin;
    private LoadedPlugin? _lease;
    private IDisposable? _emptyTimer;
    private int _nextActorId = 1;
    private int _masterClientId;
    private bool _closed;

    public Room(string name, RoomOptions options, RoomServices services, IRoomDirectory directory)
    {
        Name = name;
        _options = options;
        _services = services;
        _directory = directory;
        _hookTimeout = TimeSpan.FromMilliseconds(services.Plugins.HookTimeoutMs);
        _logger = services.LoggerFactory.CreateLogger($"NetPulse.Room.{name}");
        _fiber = new Fiber($"room:{name}", _logger, services.QueueCapacity);
        _properties = options.CustomProperties?.Clone() ?? new PropertyTable();
        _properties[RoomPropertyKeys.IsOpen] = options.IsOpen;
        _properties[RoomPropertyKeys.IsVisible] = options.IsVisible;
        _properties[RoomPropertyKeys.MaxPlayers] = (int)options.MaxPlayers;
        _currentScene = _properties.Get<string>(RoomPropertyKeys.Scene);
        _host = new PluginHost(this, _logger, services.Clock, services.HttpFactory);
        _plugin = new PassThroughPlugin(_host);
        _services.Metrics.RoomCreated();
    }

    public string Name { get; }

    public int MasterClientId => _masterClientId;

    public PropertyTable Properties => _properties;

    public Task Completion => _fiber.Completion;

    public RoomSnapshot InitialSnapshot() => BuildSnapshot();

    public Task<JoinOutcome> CreateAsync(RoomRequestContext context, PropertyTable? playerProperties) =>
        InvokeJoinAsync(() => CreateCoreAsync(context, playerProperties));

    public Task<JoinOutcome> JoinAsync(RoomRequestContext context, PropertyTable? playerProperties) =>
        InvokeJoinAsync(() => JoinCoreAsync(context, playerProperties, false));

    public void HandleOperation(Peer peer, OperationRequest request)
    {
        if (_services.Flood.TryPost(_fiber, peer, () => DispatchAsync(peer, request), $"room {Name}") == PostResult.Closed)
        {
            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.RoomNotFound, "Room is closed");
        }
    }

    public void PeerDisconnected(Peer peer) => _fiber.Post(Guard(async () =>
    {
        var actor = FindActor(peer);

        if (actor is not null)
        {
            await LeaveActorAsync(actor, true, "disconnect");
        }
    }));

    public async Task ShutdownAsync()
    {
        try
        {
            await _fiber.InvokeAsync(() => CloseCoreAsync("server shutdown", runBeforeHook: true, force: true));
        }
        catch (FiberClosedException)
        {
        }
    }

    public IReadOnlyList<IActor> ActorSnapshot() => _actors.ToArray();

    public IActor? FindActor(int actorId) => _actors.Find(a => a.ActorId == actorId);

    public void RunOnFiber(Action action)
    {
        if (!_fiber.IsCurrent)
        {
            _fiber.Post(Guard(action));
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _ = ReportExceptionAsync(ex);
        }
    }

    public void PostGuarded(Action action) => _fiber.Post(Guard(action));

    public void PostKick(int actorId, string reason) => _fiber.Post(Guard(() => KickAsync(actorId, reason)));

    public void PostClose() => _fiber.Post(Guard(() => CloseCoreAsync("closed by plugin", runBeforeHook: false, force: true)));

    public async ValueTask ReportExceptionAsync(Exception ex, ICallInfo? call = null)
    {
        _services.Metrics.PluginError();
        _logger.LogError(ex, "Exception in plugin code of room {Room}", Name);

        try
        {
            await _plugin.OnUnhandledExceptionAsync(ex, call);
        }
        catch (Exception inner)
        {
            _logger.LogError(inner, "OnUnhandledExceptionAsync itself threw in room {Room}", Name);
        }
    }

    public void ApplyPropertiesFromPlugin(int actorId, PropertyTable properties, bool broadcast)
    {
        if (_closed)
        {
            return;
        }

        if (actorId == 0)
        {
            _properties.Merge(properties);
            SyncReservedKeys(properties);
            NotifyLobby();
        }
        else
        {
            var actor = _actors.Find(a => a.ActorId == actorId);

            if (actor is null)
            {
                return;
            }

            actor.Properties.Merge(properties);
        }

        if (broadcast)
        {
            BroadcastPropertiesChanged(actorId, properties, 0);
        }
    }

    private async Task<JoinOutcome> InvokeJoinAsync(Func<ValueTask<JoinOutcome>> work)
    {
        try
        {
            return await _fiber.InvokeAsync(work);
        }
        catch (FiberClosedException)
        {
            return new JoinOutcome(ReturnCode.RoomNotFound, "Room is closed");
        }
    }

    private Func<ValueTask> Guard(Action action) => Guard(() =>
    {
        action();
        return ValueTask.CompletedTask;
    });

    private Func<ValueTask> Guard(Func<ValueTask> work) => async () =>
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            await ReportExceptionAsync(ex);
        }
    };

    private async ValueTask<PluginCall> RunHookAsync(PluginCall call, Func<IRoomPlugin, CancellationToken, ValueTask> hook)
    {
        using var hookCts = new CancellationTokenSource(_hookTimeout);
        Task task;

        try
        {
            task = hook(_plugin, hookCts.Token).AsTask();
        }
        catch (Exception ex)
        {
            await ReportExceptionAsync(ex, call);
            call.FailIfPending("Plugin error");
            return call;
        }

        if (!task.IsCompleted)
        {
            using var delayCts = new CancellationTokenSource();
            var winner = await Task.WhenAny(task, Task.Delay(_hookTimeout, delayCts.Token));

            if (!ReferenceEquals(winner, task))
            {
                call.FailIfPending("Plugin hook timed out");
                _logger.LogWarning("Plugin hook {Plugin} timed out after {Timeout} in room {Room}", _plugin.Name, _hookTimeout, Name);
                hookCts.Cancel();
                ObserveLate(task);
                return call;
            }

            delayCts.Cancel();
        }

        if (task.IsFaulted)
        {
            await ReportExceptionAsync(task.Exception!.GetBaseException(), call);
            call.FailIfPending("Plugin error");
        }
        else if (task.IsCanceled)
        {
            call.FailIfPending("Plugin hook was cancelled");
        }

        if (!call.IsProcessed)
        {
            _logger.LogError("Plugin hook returned without calling Continue/Fail in room {Room}; treating as Fail", Name);
            call.FailIfPending("Plugin did not respond");
        }

        return call;
    }

    private static void ObserveLate(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async ValueTask<JoinOutcome> InitializePluginAsync()
    {
        var name = string.IsNullOrWhiteSpace(_options.Plugin) ? _services.Plugins.DefaultPluginName : _options.Plugin;

        if (string.IsNullOrWhiteSpace(name))
        {
            return JoinOutcome.Ok;
        }

        _options.Plugin = name;
        var lease = _services.Plugins.Lease(name);

        if (lease is null)
        {
            return new JoinOutcome(ReturnCode.PluginNotFound, $"Plugin '{name}' is not installed");
        }

        try
        {
            using var cts = new CancellationTokenSource(_hookTimeout);

            if (lease.Factory is null)
            {
                lease.Release();
                return new JoinOutcome(ReturnCode.PluginNotFound, $"Plugin '{name}' provides no room plugin");
            }

            var creation = lease.Factory.CreateAsync(_host, name, _services.Plugins.GetConfig(name), cts.Token).AsTask();
            var winner = await Task.WhenAny(creation, Task.Delay(_hookTimeout));

            if (!ReferenceEquals(winner, creation))
            {
                cts.Cancel();
                ObserveLate(creation);
                lease.Release();
                return new JoinOutcome(ReturnCode.PluginReportedError, "Plugin factory timed out");
            }

            var result = await creation;

            if (result.Plugin is null)
            {
                lease.Release();
                return new JoinOutcome(ReturnCode.PluginReportedError, result.Error ?? "Plugin factory returned no plugin");
            }

            _lease = lease;
            _plugin = result.Plugin;
            return JoinOutcome.Ok;
        }
        catch (Exception ex)
        {
            lease.Release();
            _services.Metrics.PluginError();
            _logger.LogError(ex, "Plugin factory for {Plugin} threw", name);
            return new JoinOutcome(ReturnCode.PluginReportedError, "Plugin factory failed");
        }
    }

    private async ValueTask<JoinOutcome> CreateCoreAsync(RoomRequestContext context, PropertyTable? playerProperties)
    {
        var init = await InitializePluginAsync();

        if (init.Code != ReturnCode.Ok)
        {
            await CloseCoreAsync("plugin initialization failed", runBeforeHook: false, force: true);
            return init;
        }

        var peer = context.Peer;
        var call = new PluginCall("OnCreateRoom", _logger)
        {
            Options = _options,
            UserId = peer.UserId,
            NickName = peer.NickName,
            PlayerProperties = playerProperties ?? new PropertyTable()
        };

        await RunHookAsync(call, (p, ct) => p.OnCreateRoomAsync(call, ct));

        if (call.Outcome != CallOutcome.Continued)
        {
            await CloseCoreAsync("create rejected by plugin", runBeforeHook: false, force: true);
            return new JoinOutcome(ReturnCode.PluginReportedError, call.Message);
        }

        return await JoinCoreAsync(context, playerProperties, true);
    }

    private async ValueTask<JoinOutcome> JoinCoreAsync(RoomRequestContext context, PropertyTable? playerProperties, bool creating)
    {
        var peer = context.Peer;

        if (_closed)
        {
            return new JoinOutcome(ReturnCode.RoomNotFound, "Room is closed");
        }

        if (!peer.Connected)
        {
            await AfterFailedJoinAsync(creating);
            return new JoinOutcome(ReturnCode.InvalidRequest, "Peer disconnected");
        }

        var existing = _actors.Find(a => a.UserId == peer.UserId);
        var rejoin = existing is { IsInactive: true };

        if (existing is not null && !rejoin)
        {
            return new JoinOutcome(ReturnCode.InvalidRequest, "User is already in this room");
        }

        if (rejoin && _services.Limits.RequireRejoinToken && !RejoinTokenMatches(existing!, context.RejoinToken))
        {
            _services.Metrics.LimitViolated();
            await AfterFailedJoinAsync(creating);
            return new JoinOutcome(ReturnCode.Forbidden, "A valid rejoin token is required");
        }

        var rejected = CheckAdmission(rejoin);

        if (rejected != ReturnCode.Ok)
        {
            await AfterFailedJoinAsync(creating);
            return new JoinOutcome(rejected, rejected == ReturnCode.RoomFull ? "Room is full" : "Room is closed");
        }

        var before = new PluginCall("OnBeforeJoin", _logger)
        {
            ActorId = existing?.ActorId ?? 0,
            UserId = peer.UserId,
            NickName = peer.NickName,
            IsRejoin = rejoin,
            PlayerProperties = playerProperties ?? new PropertyTable()
        };

        await RunHookAsync(before, (p, ct) => p.OnBeforeJoinAsync(before, ct));

        if (before.Outcome != CallOutcome.Continued)
        {
            await AfterFailedJoinAsync(creating);
            return new JoinOutcome(ReturnCode.PluginReportedError, before.Message);
        }

        if (_closed || !peer.Connected)
        {
            await AfterFailedJoinAsync(creating);
            return new JoinOutcome(ReturnCode.RoomNotFound, "Room is closed");
        }

        var actor = AddOrReviveActor(peer, existing, rejoin, before.PlayerProperties);
        var join = new PluginCall("OnJoin", _logger)
        {
            ActorId = actor.ActorId,
            Actor = actor,
            IsRejoin = rejoin
        };

        await RunHookAsync(join, (p, ct) => p.OnJoinAsync(join, ct));

        if (join.Outcome != CallOutcome.Continued)
        {
            RollbackJoin(actor, rejoin);
            await AfterFailedJoinAsync(creating);
            return new JoinOutcome(ReturnCode.PluginReportedError, join.Message);
        }

        CompleteJoin(context, actor, rejoin);

        if (!peer.Connected)
        {
            await LeaveActorAsync(actor, true, "disconnect");
        }

        return JoinOutcome.Ok;
    }

    private RoomActor AddOrReviveActor(Peer peer, RoomActor? existing, bool rejoin, PropertyTable playerProperties)
    {
        RoomActor actor;

        if (rejoin)
        {
            actor = existing!;
            actor.TtlTimer?.Dispose();
            actor.TtlTimer = null;
            actor.IsInactive = false;
            actor.Peer = peer;
        }
        else
        {
            actor = new RoomActor(this, _nextActorId++, peer, playerProperties.Clone());
            _actors.Add(actor);
        }

        actor.RejoinToken = _services.Limits.RequireRejoinToken ? NewRejoinToken() : null;
        peer.Room = this;
        RecomputeMaster(false);
        return actor;
    }

    private static string NewRejoinToken() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));

    private static bool RejoinTokenMatches(RoomActor existing, string? presented)
    {
        if (existing.RejoinToken is null || presented is null)
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(existing.RejoinToken),
            System.Text.Encoding.UTF8.GetBytes(presented));
    }

    private short CheckAdmission(bool rejoin)
    {
        if (rejoin)
        {
            return ReturnCode.Ok;
        }

        if (!_options.IsOpen)
        {
            return ReturnCode.RoomClosed;
        }

        return _options.MaxPlayers > 0 && _actors.Count >= _options.MaxPlayers ? ReturnCode.RoomFull : ReturnCode.Ok;
    }

    private void RollbackJoin(RoomActor actor, bool rejoin)
    {
        if (actor.Peer is not null && ReferenceEquals(actor.Peer.Room, this))
        {
            actor.Peer.Room = null;
        }

        if (rejoin)
        {
            actor.IsInactive = true;
            actor.Peer = null;
            actor.TtlTimer = StartPlayerTtl(actor);
        }
        else
        {
            _actors.Remove(actor);
        }

        RecomputeMaster(false);
    }

    private async ValueTask AfterFailedJoinAsync(bool creating)
    {
        if (creating && _actors.Count == 0)
        {
            await CloseCoreAsync("create failed", runBeforeHook: false, force: true);
        }
    }

    private void CompleteJoin(RoomRequestContext context, RoomActor actor, bool rejoin)
    {
        _emptyTimer?.Dispose();
        _emptyTimer = null;

        var response = new JoinRoomResponse
        {
            RoomName = Name,
            LocalActorId = actor.ActorId,
            MasterClientId = _masterClientId,
            Players = _actors.Select(a => a.ToInfo()).ToArray(),
            RoomProperties = _properties.Clone(),
            ServerTimeTicks = _services.Clock.NowMs,
            MaxPlayers = _options.MaxPlayers,
            IsOpen = _options.IsOpen,
            IsVisible = _options.IsVisible,
            IsRejoin = rejoin,
            TickRate = _services.TickRate,
            RejoinToken = actor.RejoinToken
        };

        context.Peer.SendResponse(context.OpCode, context.RequestId, ReturnCode.Ok, null, response.ToParameters());
        SendToActive(EventCode.PlayerJoined, actor.ToInfo().ToParameters(), actor.ActorId);
        ReplayCache(actor);
        NotifyLobby();
    }

    private async ValueTask KickAsync(int actorId, string reason)
    {
        var actor = _actors.Find(a => a.ActorId == actorId);

        if (actor is null)
        {
            return;
        }

        actor.Peer?.SendEvent(EventCode.ErrorInfo, new ErrorInfoData { ErrorCode = ReturnCode.Forbidden, Message = reason }.ToParameters());
        await LeaveActorAsync(actor, false, reason);
    }

    private async ValueTask LeaveActorAsync(RoomActor actor, bool wantInactive, string reason)
    {
        var inactive = wantInactive && _options.PlayerTtlMs != 0;

        if (!_actors.Contains(actor) || (actor.IsInactive && inactive))
        {
            return;
        }

        var call = new PluginCall("OnLeave", _logger)
        {
            ActorId = actor.ActorId,
            Actor = actor,
            IsInactive = inactive,
            Reason = reason
        };

        await RunHookAsync(call, (p, ct) => p.OnLeaveAsync(call, ct));

        if (call.Outcome == CallOutcome.Failed)
        {
            _logger.LogWarning("Plugin reported failure in OnLeave: {Message}", call.Message);
        }

        await FinalizeLeaveAsync(actor, inactive);
    }

    private async ValueTask FinalizeLeaveAsync(RoomActor actor, bool inactive)
    {
        if (!_actors.Contains(actor) || (actor.IsInactive && inactive))
        {
            return;
        }

        var peer = actor.Peer;
        var returnedToMaster = new List<int>();

        if (inactive)
        {
            actor.IsInactive = true;
            actor.Peer = null;
            actor.Groups.Clear();
            actor.TtlTimer = StartPlayerTtl(actor);
        }
        else
        {
            actor.TtlTimer?.Dispose();
            _actors.Remove(actor);
            returnedToMaster = ReleaseObjectsOf(actor.ActorId);
        }

        if (peer is not null && ReferenceEquals(peer.Room, this))
        {
            peer.Room = null;
        }

        var masterChanged = RecomputeMaster(false);
        SendToActive(EventCode.PlayerLeft, new PlayerLeftData { ActorId = actor.ActorId, IsInactive = inactive, MasterClientId = _masterClientId }.ToParameters(), 0);

        if (masterChanged)
        {
            SendToActive(EventCode.MasterClientChanged, new ParameterDictionary { [ParamKey.MasterClientId] = _masterClientId }, 0);
        }

        foreach (var viewId in returnedToMaster)
        {
            SendToActive(EventCode.OwnershipChanged, new OwnershipChangedData { ViewId = viewId, NewOwnerActorId = 0, SenderActorId = 0 }.ToParameters(), 0);
        }

        NotifyLobby();
        await CheckEmptyAsync();
    }

    private IDisposable? StartPlayerTtl(RoomActor actor) =>
        _options.PlayerTtlMs <= 0 ? null : StartTimer(_options.PlayerTtlMs, () => FinalizeLeaveAsync(actor, false));

    private Timer StartTimer(int delayMs, Func<ValueTask> work)
    {
        var guarded = Guard(work);
        return new Timer(_ => _fiber.Post(guarded), null, delayMs, Timeout.Infinite);
    }

    private bool RecomputeMaster(bool announce)
    {
        var master = _actors.Find(a => !a.IsInactive)?.ActorId ?? 0;

        if (master == _masterClientId)
        {
            return false;
        }

        _masterClientId = master;

        if (announce)
        {
            SendToActive(EventCode.MasterClientChanged, new ParameterDictionary { [ParamKey.MasterClientId] = master }, 0);
        }

        return true;
    }

    private async ValueTask CheckEmptyAsync()
    {
        if (_actors.Count > 0 || _closed)
        {
            return;
        }

        if (_options.EmptyRoomTtlMs > 0)
        {
            _emptyTimer?.Dispose();
            _emptyTimer = StartTimer(_options.EmptyRoomTtlMs, () => CloseCoreAsync("empty", runBeforeHook: true, force: false));
            return;
        }

        await CloseCoreAsync("empty", runBeforeHook: true, force: false);
    }

    private async ValueTask CloseCoreAsync(string reason, bool runBeforeHook, bool force)
    {
        if (_closed)
        {
            return;
        }

        if (runBeforeHook)
        {
            var before = new PluginCall("OnBeforeCloseRoom", _logger, cancellable: true) { Reason = reason };
            await RunHookAsync(before, (p, ct) => p.OnBeforeCloseRoomAsync(before, ct));

            if (before.Outcome != CallOutcome.Continued && !force)
            {
                return;
            }
        }

        _closed = true;
        _emptyTimer?.Dispose();

        foreach (var actor in _actors.ToArray())
        {
            actor.TtlTimer?.Dispose();

            if (actor.Peer is { } peer)
            {
                peer.SendEvent(EventCode.ErrorInfo, new ErrorInfoData { ErrorCode = ReturnCode.RoomClosed, Message = reason }.ToParameters());

                if (ReferenceEquals(peer.Room, this))
                {
                    peer.Room = null;
                }
            }
        }

        _actors.Clear();
        _host.Shutdown();

        var closing = new PluginCall("OnCloseRoom", _logger) { Reason = reason };
        await RunHookAsync(closing, (p, ct) => p.OnCloseRoomAsync(closing, ct));
        await DisposePluginAsync();
        Finish();
    }

    private async ValueTask DisposePluginAsync()
    {
        try
        {
            await _plugin.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin DisposeAsync threw in room {Room}", Name);
        }
    }

    private void Finish()
    {
        _directory.RoomClosed(this);
        _services.Metrics.RoomClosed();
        _lease?.Release();
        _lease = null;
        _fiber.Complete();
        _logger.LogInformation("Room {Room} closed", Name);
    }

    private RoomSnapshot BuildSnapshot()
    {
        var lobby = new PropertyTable();

        if (_options.LobbyProperties is { } keys)
        {
            foreach (var key in keys)
            {
                if (_properties.ContainsKey(key))
                {
                    lobby[key] = _properties[key];
                }
            }
        }

        var info = new RoomInfo
        {
            Name = Name,
            PlayerCount = (byte)Math.Min(_actors.Count, 255),
            MaxPlayers = _options.MaxPlayers,
            IsOpen = _options.IsOpen,
            LobbyProperties = lobby
        };

        return new RoomSnapshot(info, _options.IsVisible);
    }

    private void NotifyLobby()
    {
        if (!_closed)
        {
            _directory.RoomUpdated(this, BuildSnapshot());
        }
    }

    private void SyncReservedKeys(PropertyTable delta)
    {
        if (delta.ContainsKey(RoomPropertyKeys.Scene))
        {
            var scene = _properties.Get<string>(RoomPropertyKeys.Scene);

            if (scene != _currentScene)
            {
                _currentScene = scene;
                PurgeSceneBoundObjects();
            }
        }

        if (delta.ContainsKey(RoomPropertyKeys.IsOpen))
        {
            _options.IsOpen = _properties.Get(RoomPropertyKeys.IsOpen, true);
        }

        if (delta.ContainsKey(RoomPropertyKeys.IsVisible))
        {
            _options.IsVisible = _properties.Get(RoomPropertyKeys.IsVisible, true);
        }

        if (delta.ContainsKey(RoomPropertyKeys.MaxPlayers))
        {
            _options.MaxPlayers = (byte)Math.Clamp(_properties.Get<int>(RoomPropertyKeys.MaxPlayers), 0, 255);
        }
    }

    private RoomActor? FindActor(Peer peer) => _actors.Find(a => ReferenceEquals(a.Peer, peer));
}
