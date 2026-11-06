using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Plugins;

namespace NetPulse.Server.Rooms;

public sealed class NetworkObjectState : INetworkObject
{
    private readonly Room _room;

    public NetworkObjectState(Room room, int viewId, int creatorActorId, int ownerActorId, ObjectOwnershipMode mode, bool isSceneObject, bool isPersistent = false)
    {
        _room = room;
        ViewId = viewId;
        CreatorActorId = creatorActorId;
        RawOwner = ownerActorId;
        OwnershipMode = mode;
        IsSceneObject = isSceneObject;
        IsPersistent = isPersistent;
    }

    public int ViewId { get; }

    public int CreatorActorId { get; }

    public int RawOwner { get; set; }

    public int OwnerActorId => RawOwner != 0 ? RawOwner : _room.MasterClientId;

    public ObjectOwnershipMode OwnershipMode { get; set; }

    public bool IsSceneObject { get; }

    public bool IsPersistent { get; }
}

public sealed partial class Room
{
    private const int ViewIdsPerActor = 1000;

    private readonly Dictionary<int, NetworkObjectState> _objects = new();
    private readonly Dictionary<(int ViewId, int Requester), IDisposable> _pendingRequests = new();

    public INetworkObject? FindObject(int viewId) => _objects.GetValueOrDefault(viewId);

    public IReadOnlyCollection<INetworkObject> ObjectSnapshot() => _objects.Values.ToArray();

    private static bool IsSceneViewId(int viewId) => viewId is > 0 and < ViewIdsPerActor;

    private NetworkObjectState? GetObject(int viewId)
    {
        if (_objects.TryGetValue(viewId, out var existing))
        {
            return existing;
        }

        if (!IsSceneViewId(viewId))
        {
            return null;
        }

        var scene = new NetworkObjectState(this, viewId, 0, 0, ObjectOwnershipMode.Fixed, true);
        _objects[viewId] = scene;
        return scene;
    }

    private bool ValidateObjectEvent(RoomRequestContext context, RoomActor actor, RaiseEventRequest request)
    {
        switch (request.EventCode)
        {
            case EventCode.ObjectInstantiate:
                if (request.ViewId < ViewIdsPerActor || request.ViewId / ViewIdsPerActor != actor.ActorId)
                {
                    RejectObjectOperation(context, actor, $"ViewId {request.ViewId} does not belong to actor {actor.ActorId}");
                    return false;
                }

                if (_objects.ContainsKey(request.ViewId))
                {
                    RejectObjectOperation(context, actor, $"ViewId {request.ViewId} already exists");
                    return false;
                }

                return true;
            case EventCode.ObjectDestroy:
                var victim = GetObject(request.ViewId);

                if (victim is null)
                {
                    RespondIfRequested(context, ReturnCode.Ok);
                    return false;
                }

                if (victim.IsSceneObject || (victim.OwnerActorId != actor.ActorId && actor.ActorId != _masterClientId))
                {
                    RejectObjectOperation(context, actor, $"Only the owner or the master client can destroy view {request.ViewId}");
                    return false;
                }

                return true;
            case EventCode.SyncVarSet:
                if (actor.ActorId != _masterClientId)
                {
                    RejectObjectOperation(context, actor, "Only the master client can set master sync variables");
                    return false;
                }

                if (GetObject(request.ViewId) is null)
                {
                    RespondIfRequested(context, ReturnCode.Ok);
                    return false;
                }

                return true;
            case EventCode.StateSync:
                var source = GetObject(request.ViewId);

                if (source is null)
                {
                    RespondIfRequested(context, ReturnCode.Ok);
                    return false;
                }

                if (source.OwnerActorId != actor.ActorId)
                {
                    RejectObjectOperation(context, actor, $"State of view {request.ViewId} is owned by actor {source.OwnerActorId}");
                    return false;
                }

                return true;
            default:
                return true;
        }
    }

    private void RejectObjectOperation(RoomRequestContext context, RoomActor actor, string message)
    {
        _services.Metrics.OwnershipViolation();
        var now = _services.Clock.NowMs;

        if (now - actor.LastViolationMs >= 1000)
        {
            actor.LastViolationMs = now;
            _logger.LogWarning("Rejected object operation from actor {Actor} in room {Room}: {Message}", actor.ActorId, Name, message);

            if (context.RequestId == 0)
            {
                actor.Peer?.SendEvent(EventCode.ErrorInfo, new ErrorInfoData { ErrorCode = ReturnCode.Forbidden, Message = message }.ToParameters());
            }
        }

        if (context.RequestId != 0)
        {
            Respond(context, ReturnCode.Forbidden, message);
        }
    }

    private void TrackObjectEvent(RaiseEventRequest request, int senderActorId)
    {
        if (request.EventCode == EventCode.ObjectInstantiate && senderActorId > 0 && request.ViewId >= ViewIdsPerActor && !_objects.ContainsKey(request.ViewId))
        {
            _objects[request.ViewId] = new NetworkObjectState(this, request.ViewId, senderActorId, senderActorId, request.OwnershipMode, false, (request.ObjectFlags & ObjectFlag.PersistAcrossScenes) != 0);
            return;
        }

        if (request.EventCode == EventCode.ObjectDestroy && request.ViewId != 0 && _objects.TryGetValue(request.ViewId, out var removed) && !removed.IsSceneObject)
        {
            _objects.Remove(request.ViewId);
            CancelPending(request.ViewId);
        }
    }

    private void PurgeSceneBoundObjects()
    {
        var removed = new HashSet<int>();

        foreach (var obj in _objects.Values.ToArray())
        {
            if (obj.IsSceneObject || !obj.IsPersistent)
            {
                removed.Add(obj.ViewId);
                _objects.Remove(obj.ViewId);
                CancelPending(obj.ViewId);
            }
        }

        _cache.RemoveAll(e => e.ViewId != 0 && (removed.Contains(e.ViewId) || IsSceneViewId(e.ViewId)));
    }

    private async ValueTask HandleTransferOwnershipAsync(RoomRequestContext context, RoomActor actor, TransferOwnershipRequest request)
    {
        var obj = GetObject(request.ViewId);

        if (obj is null)
        {
            Respond(context, ReturnCode.ObjectNotFound, "Unknown view id");
            return;
        }

        if (obj.OwnershipMode == ObjectOwnershipMode.Fixed)
        {
            RejectObjectOperation(context, actor, $"View {request.ViewId} has fixed ownership");
            return;
        }

        var target = request.NewOwnerActorId;

        if (target == 0 ? !obj.IsSceneObject : !_actors.Exists(a => a.ActorId == target && !a.IsInactive))
        {
            Respond(context, ReturnCode.ActorNotFound, "New owner not found");
            return;
        }

        var allowed = actor.ActorId == obj.OwnerActorId
            || actor.ActorId == _masterClientId
            || (obj.OwnershipMode == ObjectOwnershipMode.Takeover && target == actor.ActorId);

        if (!allowed)
        {
            RejectObjectOperation(context, actor, $"Actor {actor.ActorId} may not transfer view {request.ViewId}");
            return;
        }

        await ApplyOwnershipAsync(context, actor, obj, target, false);
    }

    private async ValueTask HandleRequestOwnershipAsync(RoomRequestContext context, RoomActor actor, RequestOwnershipRequest request)
    {
        var obj = GetObject(request.ViewId);

        if (obj is null)
        {
            Respond(context, ReturnCode.ObjectNotFound, "Unknown view id");
            return;
        }

        if (obj.OwnershipMode == ObjectOwnershipMode.Fixed)
        {
            RejectObjectOperation(context, actor, $"View {request.ViewId} has fixed ownership");
            return;
        }

        if (obj.OwnerActorId == actor.ActorId)
        {
            Respond(context, ReturnCode.Ok);
            return;
        }

        if (obj.OwnershipMode == ObjectOwnershipMode.Takeover)
        {
            await ApplyOwnershipAsync(context, actor, obj, actor.ActorId, false);
            return;
        }

        if (_pendingRequests.ContainsKey((obj.ViewId, actor.ActorId)))
        {
            Respond(context, ReturnCode.Ok);
            return;
        }

        var call = NewTransferCall(actor, obj, actor.ActorId, true);
        await RunHookAsync(call, (p, ct) => p.OnBeforeTransferOwnershipAsync(call, ct));

        if (!await ResolveTransferHookAsync(context, call))
        {
            return;
        }

        var owner = _actors.Find(a => a.ActorId == obj.OwnerActorId && !a.IsInactive);
        var key = (obj.ViewId, actor.ActorId);
        _pendingRequests[key] = StartTimer(_services.OwnershipRequestTimeoutMs, () =>
        {
            _pendingRequests.Remove(key);
            return ValueTask.CompletedTask;
        });

        owner?.Peer?.SendEvent(EventCode.OwnershipRequested, new OwnershipRequestedData { ViewId = obj.ViewId, RequesterActorId = actor.ActorId }.ToParameters());
        Respond(context, ReturnCode.Ok);
    }

    private ValueTask HandleRegisterSceneObjectsAsync(RoomRequestContext context, RoomActor actor, RegisterSceneObjectsRequest request)
    {
        if (actor.ActorId != _masterClientId)
        {
            RejectObjectOperation(context, actor, "Only the master client can register scene objects");
            return ValueTask.CompletedTask;
        }

        var count = Math.Min(request.ViewIds.Length, request.Modes.Length);

        for (var i = 0; i < count; i++)
        {
            var obj = GetObject(request.ViewIds[i]);

            if (obj is { IsSceneObject: true } && request.Modes[i] <= (byte)ObjectOwnershipMode.Request)
            {
                obj.OwnershipMode = (ObjectOwnershipMode)request.Modes[i];
            }
        }

        Respond(context, ReturnCode.Ok);
        return ValueTask.CompletedTask;
    }

    private PluginCall NewTransferCall(RoomActor actor, NetworkObjectState obj, int newOwner, bool isRequest) =>
        new("OnBeforeTransferOwnership", _logger, cancellable: true)
        {
            ActorId = actor.ActorId,
            ViewId = obj.ViewId,
            NewOwnerActorId = newOwner,
            CurrentOwnerActorId = obj.OwnerActorId,
            OwnershipMode = obj.OwnershipMode,
            IsRequest = isRequest
        };

    private ValueTask<bool> ResolveTransferHookAsync(RoomRequestContext context, PluginCall call)
    {
        switch (call.Outcome)
        {
            case CallOutcome.Continued:
                return ValueTask.FromResult(true);
            case CallOutcome.Cancelled:
                Respond(context, ReturnCode.Ok);
                break;
            default:
                Respond(context, ReturnCode.PluginReportedError, call.Message);
                break;
        }

        return ValueTask.FromResult(false);
    }

    private async ValueTask ApplyOwnershipAsync(RoomRequestContext context, RoomActor actor, NetworkObjectState obj, int newOwner, bool isRequest)
    {
        var call = NewTransferCall(actor, obj, newOwner, isRequest);
        await RunHookAsync(call, (p, ct) => p.OnBeforeTransferOwnershipAsync(call, ct));

        if (!await ResolveTransferHookAsync(context, call))
        {
            return;
        }

        if (!_objects.TryGetValue(obj.ViewId, out var current) || !ReferenceEquals(current, obj))
        {
            Respond(context, ReturnCode.ObjectNotFound, "Object was destroyed");
            return;
        }

        obj.RawOwner = newOwner;
        CancelPending(obj.ViewId);
        var data = new OwnershipChangedData { ViewId = obj.ViewId, NewOwnerActorId = newOwner, SenderActorId = actor.ActorId };
        SendToActive(EventCode.OwnershipChanged, data.ToParameters(), 0);
        Respond(context, ReturnCode.Ok);
    }

    private void CancelPending(int viewId)
    {
        foreach (var key in _pendingRequests.Keys.Where(k => k.ViewId == viewId).ToArray())
        {
            _pendingRequests[key].Dispose();
            _pendingRequests.Remove(key);
        }
    }

    private void CancelPendingOf(int actorId)
    {
        foreach (var key in _pendingRequests.Keys.Where(k => k.Requester == actorId).ToArray())
        {
            _pendingRequests[key].Dispose();
            _pendingRequests.Remove(key);
        }

        foreach (var viewId in _objects.Values.Where(o => o.OwnerActorId == actorId).Select(o => o.ViewId).ToArray())
        {
            CancelPending(viewId);
        }
    }

    private List<int> ReleaseObjectsOf(int actorId)
    {
        var returnedToMaster = new List<int>();
        CancelPendingOf(actorId);

        foreach (var obj in _objects.Values.Where(o => o.RawOwner == actorId).ToArray())
        {
            if (obj.IsSceneObject)
            {
                obj.RawOwner = 0;
                returnedToMaster.Add(obj.ViewId);
                continue;
            }

            _objects.Remove(obj.ViewId);
            _cache.RemoveAll(e => e.ViewId == obj.ViewId);
        }

        _cache.RemoveAll(e => e.Sender == actorId && !e.IsGlobal && (e.ViewId == 0 || !_objects.ContainsKey(e.ViewId)));
        return returnedToMaster;
    }
}
