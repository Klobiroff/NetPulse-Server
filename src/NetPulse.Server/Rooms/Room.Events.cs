using NetPulse.Common;
using NetPulse.Server.Networking;

namespace NetPulse.Server.Rooms;

public sealed partial class Room
{
    public void Deliver(RaiseEventRequest request, int senderActorId)
    {
        if (_closed)
        {
            return;
        }

        var isDestroy = request.EventCode == EventCode.ObjectDestroy;
        var caching = isDestroy ? CachingOption.DoNotCache : request.Caching;

        if (isDestroy && request.ViewId != 0)
        {
            _cache.RemoveAll(e => e.ViewId == request.ViewId);
        }

        TrackObjectEvent(request, senderActorId);

        if (caching == CachingOption.RemoveFromRoomCache)
        {
            _cache.RemoveAll(e => e.Code == request.EventCode && (request.ViewId == 0 || e.ViewId == request.ViewId));
            return;
        }

        if (caching == CachingOption.RemoveFromCacheForActorsLeft)
        {
            _cache.RemoveAll(e => e.Sender != 0 && !_actors.Exists(a => a.ActorId == e.Sender) && !(e.ViewId != 0 && _objects.ContainsKey(e.ViewId)));
            return;
        }

        if (caching is CachingOption.AddToRoomCache or CachingOption.AddToRoomCacheGlobal)
        {
            _cache.Add(new CachedEvent
            {
                Code = request.EventCode,
                Data = request.Data,
                Sender = senderActorId,
                ViewId = request.ViewId,
                Group = request.InterestGroup,
                IsGlobal = caching == CachingOption.AddToRoomCacheGlobal || senderActorId == 0
            });
        }

        var payload = new RoomEventData { SenderActorId = senderActorId, Data = request.Data, ViewId = request.ViewId };
        var packet = Wire.Event(request.EventCode, payload.ToParameters());

        foreach (var actor in _actors)
        {
            if (actor.Peer is null || actor.IsInactive || !IsReceiver(actor, request, senderActorId))
            {
                continue;
            }

            actor.Peer.Send(packet, request.Delivery);
        }
    }

    private bool IsReceiver(RoomActor actor, RaiseEventRequest request, int senderActorId)
    {
        switch (request.Receivers)
        {
            case ReceiverGroup.MasterClient:
                return actor.ActorId == _masterClientId;
            case ReceiverGroup.Targets:
                return request.TargetActors is { } targets && Array.IndexOf(targets, actor.ActorId) >= 0;
            case ReceiverGroup.Others when actor.ActorId == senderActorId:
                return false;
        }

        return request.InterestGroup == 0 || actor.Groups.Contains(request.InterestGroup);
    }

    private void SendToActive(byte code, ParameterDictionary parameters, int exceptActorId)
    {
        var packet = Wire.Event(code, parameters);

        foreach (var actor in _actors)
        {
            if (actor.Peer is not null && !actor.IsInactive && actor.ActorId != exceptActorId)
            {
                actor.Peer.Send(packet);
            }
        }
    }

    private void BroadcastPropertiesChanged(int actorId, PropertyTable delta, int senderActorId)
    {
        var data = new PropertiesChangedData { ActorId = actorId, Properties = delta, SenderActorId = senderActorId };
        var code = actorId == 0 ? EventCode.RoomPropertiesChanged : EventCode.PlayerPropertiesChanged;
        SendToActive(code, data.ToParameters(), 0);
    }

    private void ReplayCache(RoomActor actor)
    {
        var peer = actor.Peer;

        if (peer is null)
        {
            return;
        }

        foreach (var cached in _cache)
        {
            if (cached.Group != 0 && !actor.Groups.Contains(cached.Group))
            {
                continue;
            }

            var payload = new RoomEventData { SenderActorId = cached.Sender, Data = cached.Data, ViewId = cached.ViewId };
            peer.SendEvent(cached.Code, payload.ToParameters());
        }

        foreach (var obj in _objects.Values)
        {
            var changed = obj.IsSceneObject ? obj.RawOwner != 0 : obj.RawOwner != obj.CreatorActorId;

            if (changed)
            {
                var data = new OwnershipChangedData { ViewId = obj.ViewId, NewOwnerActorId = obj.RawOwner, SenderActorId = 0 };
                peer.SendEvent(EventCode.OwnershipChanged, data.ToParameters());
            }
        }
    }
}
