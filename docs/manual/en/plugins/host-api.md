# Host API

`IPluginHost` is what a plugin uses to read and change its room. Each room has its own host, and `RoomPlugin` exposes it as `Host`. The factory receives the same host in `CreateAsync`.

## Reading the room

| Member | Type | Description |
|---|---|---|
| `RoomName` | `string` | Name of the room. |
| `Actors` | `IReadOnlyList<IActor>` | A snapshot of every actor in the room, including inactive ones, in join order. |
| `MasterClientId` | `int` | Actor id of the master client, or 0 when nobody is active. The master is the longest-present active player. |
| `RoomProperties` | `PropertyTable` | The live room properties. Read it freely; change it through `SetRoomProperties` so that clients are told. |
| `ServerTimeMs` | `long` | Milliseconds since the server started. It is the same clock that clients receive as server time. |
| `Logger` | `ILogger` | Logger in the category `NetPulse.Room.<room name>`. |
| `GetActor(actorId)` | `IActor?` | One actor, or `null`. |
| `GetNetworkObject(viewId)` | `INetworkObject?` | One registered network object, or `null`. |
| `NetworkObjects` | `IReadOnlyCollection<INetworkObject>` | A snapshot of all registered network objects. |

`IActor` has `ActorId`, `UserId`, `NickName`, `Properties`, `IsInactive` and `IsMasterClient`. An inactive actor is a player who disconnected and may rejoin; it receives no events.

`INetworkObject` has `ViewId`, `CreatorActorId`, `OwnerActorId`, `OwnershipMode`, `IsSceneObject` and `IsPersistent`. For a scene object nobody has taken, `OwnerActorId` reports the current master.

!!! warning "Read room state only on the room fiber"
    `Actors`, `GetActor`, `RoomProperties`, `NetworkObjects` and `GetNetworkObject` read the room directly. Use them inside hooks, timer callbacks, `Enqueue` actions and after an `await` in a hook. Do not touch them from `RunInBackground` or from a thread you started. `Logger`, `ServerTimeMs`, `RoomName` and `HttpAsync` are safe anywhere.

## Acting on the room

| Member | Description |
|---|---|
| `BroadcastEvent(code, data, receivers, targets, caching, delivery, senderActorId)` | Send an event to players. |
| `SetRoomProperties(props, broadcast)` | Merge properties into the room. |
| `SetPlayerProperties(actorId, props, broadcast)` | Merge properties into one player. |
| `KickActor(actorId, reason)` | Remove a player from the room. |
| `CloseRoom()` | Close the room. |
| `CreateTimer(...)`, `Enqueue(...)`, `HttpAsync(...)`, `RunInBackground(...)` | See [Timers, HTTP and background work](timers-http-background.md). |

These methods are safe to call from any thread. Called from the room fiber, they take effect immediately. Called from elsewhere, they are queued and run on the fiber.

### BroadcastEvent

```csharp
void BroadcastEvent(
    byte code,
    object? data,
    ReceiverGroup receivers = ReceiverGroup.All,
    int[]? targets = null,
    CachingOption caching = CachingOption.DoNotCache,
    DeliveryMode delivery = DeliveryMode.ReliableOrdered,
    int senderActorId = 0);
```

- `code` is the event code. Use 0 to 199 for your own events, the same range clients may raise.
- `data` is any value the wire format supports: numbers, strings, arrays, `PropertyTable`, or types registered with `NetTypeRegistry`. See [Wire protocol](../reference/protocol.md#value-types).
- `receivers` is `Others`, `All`, `MasterClient` or `Targets`. `Targets` sends to the actor ids listed in `targets`. Inactive actors never receive events.
- `caching` controls the event cache that late joiners receive. An event cached with `senderActorId` 0 belongs to the server and stays until removed, even after players leave.
- `senderActorId` is what clients see as the sender. 0 means the server. With sender 0, `Others` reaches everyone.

Events from a plugin skip `OnRaiseEventAsync`. Clients receive them like any other event.

### SetRoomProperties and SetPlayerProperties

Both merge the table into the existing properties; a `null` value deletes a key. With `broadcast` true (the default) all players receive a properties-changed event; the sender id in that event is 0 (the server).

These calls do not run `OnBeforeSetPropertiesAsync` and are not subject to the reserved-key rule, so a plugin can set `_isOpen`, `_isVisible`, `_maxPlayers` and `_scene` itself. The lobby list is updated after a room change. `SetPlayerProperties` ignores an unknown actor id.

### KickActor and CloseRoom

`KickActor` tells the player with an `ErrorInfo` event (`Forbidden` and your reason), then removes it from the room. `OnLeaveAsync` runs with the reason you gave and `IsInactive` false. The player stays connected to the server and can join another room.

`CloseRoom` sends every player an `ErrorInfo` event with `RoomClosed`, then closes the room. `OnBeforeCloseRoomAsync` is skipped and `OnCloseRoomAsync` runs with the reason `closed by plugin`.

## Example

A plugin that lets the master client run two commands with event 70: `kick <actorId>` and `close`. It also stamps each joining player with the join time as a player property.

```csharp title="AdminCommandsPlugin.cs"
using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;

namespace HostExamples;

public sealed class AdminCommandsPlugin : RoomPlugin
{
    public const byte CommandEvent = 70;
    public const byte NoticeEvent = 71;

    public AdminCommandsPlugin(IPluginHost host)
        : base(host)
    {
    }

    public override ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct)
    {
        Host.SetPlayerProperties(info.Actor.ActorId, new PropertyTable { ["joinedAtMs"] = Host.ServerTimeMs });
        info.Continue();
        return default;
    }

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        if (info.Request.EventCode != CommandEvent)
        {
            info.Continue();
            return default;
        }

        if (info.ActorId != Host.MasterClientId)
        {
            info.Fail("Only the master client can run commands");
            return default;
        }

        Run(info.Request.Data as string ?? string.Empty);
        info.Cancel();
        return default;
    }

    private void Run(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 2 && parts[0] == "kick" && int.TryParse(parts[1], out var actorId) && Host.GetActor(actorId) is { } target)
        {
            Host.BroadcastEvent(NoticeEvent, $"{target.NickName} was kicked");
            Host.KickActor(actorId, "Kicked by the master client");
            return;
        }

        if (parts.Length == 1 && parts[0] == "close")
        {
            Host.Logger.LogInformation("Room {Room} closed by command", Host.RoomName);
            Host.CloseRoom();
            return;
        }

        Host.Logger.LogWarning("Unknown command '{Command}'", command);
    }
}
```

The command event is cancelled, so it never reaches the other players, and the sender gets `Ok` when it asked for a response.
