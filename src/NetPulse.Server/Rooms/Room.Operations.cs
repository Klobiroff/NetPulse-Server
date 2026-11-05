using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Server.Networking;
using NetPulse.Server.Plugins;

namespace NetPulse.Server.Rooms;

public sealed partial class Room
{
    private async ValueTask DispatchAsync(Peer peer, OperationRequest request)
    {
        var actor = FindActor(peer);

        if (actor is null)
        {
            peer.SendResponse(request.OpCode, request.RequestId, ReturnCode.NotInRoom, "Not in this room");
            return;
        }

        var context = new RoomRequestContext(peer, request.OpCode, request.RequestId);

        try
        {
            switch (request.OpCode)
            {
                case OpCode.LeaveRoom:
                    var leave = LeaveRoomRequest.FromParameters(request.Parameters);
                    await LeaveActorAsync(actor, leave.BecomeInactive, "left");
                    Respond(context, ReturnCode.Ok);
                    break;
                case OpCode.RaiseEvent:
                    await HandleRaiseEventAsync(context, actor, RaiseEventRequest.FromParameters(request.Parameters));
                    break;
                case OpCode.SetRoomProperties:
                case OpCode.SetPlayerProperties:
                    await HandleSetPropertiesAsync(context, actor, SetPropertiesRequest.FromParameters(request.Parameters), request.OpCode == OpCode.SetPlayerProperties);
                    break;
                case OpCode.ChangeInterestGroups:
                    HandleInterestGroups(context, actor, ChangeInterestGroupsRequest.FromParameters(request.Parameters));
                    break;
                case OpCode.TransferOwnership:
                    await HandleTransferOwnershipAsync(context, actor, TransferOwnershipRequest.FromParameters(request.Parameters));
                    break;
                case OpCode.RequestOwnership:
                    await HandleRequestOwnershipAsync(context, actor, RequestOwnershipRequest.FromParameters(request.Parameters));
                    break;
                case OpCode.RegisterSceneObjects:
                    await HandleRegisterSceneObjectsAsync(context, actor, RegisterSceneObjectsRequest.FromParameters(request.Parameters));
                    break;
                default:
                    Respond(context, ReturnCode.InvalidRequest, "Unknown room operation");
                    break;
            }
        }
        catch (NetSerializationException ex)
        {
            Respond(context, ReturnCode.InvalidRequest, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentException or FormatException)
        {
            Respond(context, ReturnCode.InvalidRequest, "Malformed request");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operation {Op} failed in room {Room}", request.OpCode, Name);
            Respond(context, ReturnCode.InternalError, "Internal error");
        }
    }

    private static void Respond(RoomRequestContext context, short code, string? message = null) =>
        context.Peer.SendResponse(context.OpCode, context.RequestId, code, message);

    private static void RespondIfRequested(RoomRequestContext context, short code, string? message = null)
    {
        if (context.RequestId != 0 || code != ReturnCode.Ok)
        {
            Respond(context, code, message);
        }
    }

    private static bool IsClientEventAllowed(byte code) =>
        code <= EventCode.MaxUserEvent
        || code is EventCode.ObjectInstantiate or EventCode.ObjectDestroy or EventCode.Rpc or EventCode.StateSync;

    private async ValueTask HandleRaiseEventAsync(RoomRequestContext context, RoomActor actor, RaiseEventRequest request)
    {
        if (!IsClientEventAllowed(request.EventCode))
        {
            Respond(context, ReturnCode.Forbidden, "Reserved event code");
            return;
        }

        if (!ValidateObjectEvent(context, actor, request))
        {
            return;
        }

        var call = new PluginCall("OnRaiseEvent", _logger, cancellable: true) { ActorId = actor.ActorId, Request = request };
        await RunHookAsync(call, (p, ct) => p.OnRaiseEventAsync(call, ct));

        switch (call.Outcome)
        {
            case CallOutcome.Continued:
                Deliver(call.Request, actor.ActorId);
                RespondIfRequested(context, ReturnCode.Ok);
                break;
            case CallOutcome.Cancelled:
                RespondIfRequested(context, ReturnCode.Ok);
                break;
            default:
                Respond(context, ReturnCode.PluginReportedError, call.Message);
                break;
        }
    }

    private async ValueTask HandleSetPropertiesAsync(RoomRequestContext context, RoomActor sender, SetPropertiesRequest request, bool forPlayer)
    {
        var targetActorId = 0;

        if (forPlayer)
        {
            targetActorId = request.ActorId == 0 ? sender.ActorId : request.ActorId;

            if (targetActorId != sender.ActorId && sender.ActorId != _masterClientId)
            {
                Respond(context, ReturnCode.Forbidden, "Only the master client can change other players' properties");
                return;
            }

            if (_actors.Find(a => a.ActorId == targetActorId) is null)
            {
                Respond(context, ReturnCode.ActorNotFound, "Actor not found");
                return;
            }
        }
        else if (sender.ActorId != _masterClientId && ContainsReservedKey(request.Properties))
        {
            Respond(context, ReturnCode.Forbidden, "Only the master client can change room state");
            return;
        }

        var before = new PluginCall("OnBeforeSetProperties", _logger, cancellable: true)
        {
            ActorId = sender.ActorId,
            TargetActorId = targetActorId,
            Properties = request.Properties ?? new PropertyTable(),
            ExpectedProperties = request.ExpectedProperties
        };

        await RunHookAsync(before, (p, ct) => p.OnBeforeSetPropertiesAsync(before, ct));

        switch (before.Outcome)
        {
            case CallOutcome.Cancelled:
                Respond(context, ReturnCode.Ok);
                return;
            case CallOutcome.Continued:
                await ApplyPropertiesAsync(context, sender, before, request.Broadcast);
                return;
            default:
                Respond(context, ReturnCode.PluginReportedError, before.Message);
                return;
        }
    }

    private async ValueTask ApplyPropertiesAsync(RoomRequestContext context, RoomActor sender, PluginCall before, bool broadcast)
    {
        var target = before.TargetActorId == 0 ? null : _actors.Find(a => a.ActorId == before.TargetActorId);

        if (before.TargetActorId != 0 && target is null)
        {
            Respond(context, ReturnCode.ActorNotFound, "Actor not found");
            return;
        }

        var table = target?.Properties ?? _properties;

        if (!table.Matches(before.ExpectedProperties))
        {
            Respond(context, ReturnCode.PropertyCasFailed, "Expected properties did not match");
            return;
        }

        table.Merge(before.Properties);

        if (target is null)
        {
            SyncReservedKeys(before.Properties);
        }

        var after = new PluginCall("OnSetProperties", _logger)
        {
            ActorId = sender.ActorId,
            TargetActorId = before.TargetActorId,
            Properties = before.Properties
        };

        await RunHookAsync(after, (p, ct) => p.OnSetPropertiesAsync(after, ct));

        if (after.Outcome == CallOutcome.Failed)
        {
            _logger.LogWarning("Plugin reported failure in OnSetProperties: {Message}", after.Message);
        }

        if (broadcast)
        {
            BroadcastPropertiesChanged(before.TargetActorId, before.Properties, sender.ActorId);
        }

        if (target is null)
        {
            NotifyLobby();
        }

        Respond(context, ReturnCode.Ok);
    }

    private static bool ContainsReservedKey(PropertyTable? properties) =>
        properties is not null
        && (properties.ContainsKey(RoomPropertyKeys.IsOpen)
            || properties.ContainsKey(RoomPropertyKeys.IsVisible)
            || properties.ContainsKey(RoomPropertyKeys.MaxPlayers));

    private void HandleInterestGroups(RoomRequestContext context, RoomActor actor, ChangeInterestGroupsRequest request)
    {
        foreach (var group in request.Remove ?? Array.Empty<byte>())
        {
            actor.Groups.Remove(group);
        }

        foreach (var group in request.Add ?? Array.Empty<byte>())
        {
            if (group != 0)
            {
                actor.Groups.Add(group);
            }
        }

        Respond(context, ReturnCode.Ok);
    }
}
