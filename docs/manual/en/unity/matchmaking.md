# Matchmaking

Matchmaking is how players end up in the same room: browse a room list in the lobby, join a room by name, create one, or let the server pick a random room that matches your filter. Every call returns a `NetOperation<Room>`, so you can `await` it. Call these after `Pulse.Connect()` has completed ([connecting](connecting.md)).

## The lobby and the room list

Joining the lobby is optional. Do it when you want a live room browser:

```csharp
using System.Collections.Generic;
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class RoomBrowser : NetPulseCallbacks
{
    private async void Start()
    {
        await Pulse.Connect();
        await Pulse.JoinLobby();
    }

    public override void OnRoomListUpdate(IReadOnlyList<RoomInfo> rooms)
    {
        foreach (var room in rooms)
        {
            var map = room.LobbyProperties?.Get<string>("map");
            Debug.Log($"{room.Name} {room.PlayerCount}/{room.MaxPlayers} open={room.IsOpen} map={map}");
        }
    }
}
```

`JoinLobby()` works from the `Connected` state and moves the client to `InLobby`. The server sends the full list at once and then deltas about once a second. The client merges them, and `Pulse.LobbyRooms` always holds the current list sorted by name. `OnRoomListUpdate` fires after each update with that merged list.

If you only need a snapshot, call `GetRoomList()` from `Connected` or `InLobby`. It returns `NetOperation<IReadOnlyList<RoomInfo>>`. `LeaveLobby()` stops the updates and clears `LobbyRooms`.

A `RoomInfo` has `Name`, `PlayerCount`, `MaxPlayers`, `IsOpen` and `LobbyProperties`. The list contains visible rooms only. `LobbyProperties` holds just the custom properties whose keys the room creator listed in `RoomOptions.LobbyProperties`. This keeps the list small and is also the set that random matchmaking filters on (below).

Joining a room from the lobby leaves the client `InRoom`. After you leave the room the state is `Connected`, so call `JoinLobby()` again to resume the list.

## RoomOptions

`RoomOptions` (in `NetPulse.Common`) describes a room at creation time:

| Property | Default | Meaning |
|---|---|---|
| `MaxPlayers` | `0` | Player limit, `0` for unlimited. A `byte`, so at most 255. |
| `IsVisible` | `true` | Whether the room appears in the room list. |
| `IsOpen` | `true` | Whether new players may join. |
| `CustomProperties` | none | Initial room properties ([rooms and properties](rooms-and-properties.md)). |
| `LobbyProperties` | none | Keys of `CustomProperties` that are copied into `RoomInfo.LobbyProperties`. |
| `Plugin` | none | Name of the server plugin that runs this room ([plugins](../plugins/overview.md)). |
| `PlayerTtlMs` | `0` | How long the server keeps the slot of a disconnected player. See below. |
| `EmptyRoomTtlMs` | `0` | How long an empty room stays alive. `0` closes it at once. |

The room can change `IsOpen`, `IsVisible` and `MaxPlayers` later through the reserved room properties `_isOpen`, `_isVisible` and `_maxPlayers`, which only the master client may set. For example, `room.SetProperty("_isOpen", false)` stops new joins once a match starts.

## Create, join, join or create

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class JoinExample : MonoBehaviour
{
    private async void Start()
    {
        await Pulse.Connect();

        var options = new RoomOptions
        {
            MaxPlayers = 4,
            CustomProperties = new PropertyTable { ["map"] = "forest", ["mode"] = "ctf" },
            LobbyProperties = new[] { "map", "mode" }
        };

        var room = await Pulse.JoinOrCreateRoom("arena", options);
        Debug.Log($"{room.Name}: {room.PlayerCount} players, master is {room.MasterClient.NickName}");
    }
}
```

| Call | Behavior | Typical failure codes |
|---|---|---|
| `CreateRoom(name, options, playerProperties)` | Creates the room. An empty name makes the server pick one. | `RoomAlreadyExists` |
| `JoinRoom(name, playerProperties)` | Joins an existing room. | `RoomNotFound`, `RoomFull`, `RoomClosed` |
| `JoinOrCreateRoom(name, options, playerProperties)` | Joins the room if it exists, creates it with `options` otherwise. | `RoomFull`, `RoomClosed` |
| `JoinRandomRoom(expectedProperties, expectedMaxPlayers, createOptions, playerProperties)` | Joins a random matching room. | `NoMatchFound` |

All arguments after the name are optional. `playerProperties` is a `PropertyTable` that becomes the local player's properties at the moment of joining, so other clients see them in `OnPlayerEnteredRoom`. The codes are `ReturnCode` values from `NetPulse.Common`; the full list is in [codes and keys](../reference/codes.md).

A successful call completes with the `Room`. By then `Pulse.CurrentRoom`, `Pulse.LocalPlayer` and the player list are filled, the server clock is synchronized, and the state is `InRoom`. Events fire in this order: `OnCreatedRoom` (only if this call created the room), then `OnJoinedRoom`, then the operation completes. A failed call raises one of `OnCreateRoomFailed`, `OnJoinRoomFailed` or `OnJoinRandomFailed` with the code and message, and the awaited operation throws a `NetPulseException`. A user can be in only one room at a time, and joining while already in a room fails.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class JoinWithErrors : MonoBehaviour
{
    private async void Start()
    {
        await Pulse.Connect();

        try
        {
            await Pulse.JoinRoom("arena");
        }
        catch (NetPulseException e) when (e.ReturnCode == ReturnCode.RoomNotFound)
        {
            await Pulse.CreateRoom("arena", new RoomOptions { MaxPlayers = 4 });
        }
        catch (NetPulseException e)
        {
            Debug.LogError($"Join failed ({e.ReturnCode}): {e.Message}");
        }
    }
}
```

## Joining a random room

`JoinRandomRoom` looks at rooms that are visible, open and not full. It keeps those whose `LobbyProperties` match `expectedProperties` (every listed key must be equal) and whose `MaxPlayers` equals `expectedMaxPlayers` (`0` means any), picks one at random and tries to join it. If nothing fits, it creates a new room with `createOptions` and a generated name, or fails with `NoMatchFound` when `createOptions` is `null`.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class QuickMatch : MonoBehaviour
{
    private async void Start()
    {
        await Pulse.Connect();

        var filter = new PropertyTable { ["mode"] = "ctf" };
        var fallback = new RoomOptions
        {
            MaxPlayers = 8,
            CustomProperties = new PropertyTable { ["mode"] = "ctf" },
            LobbyProperties = new[] { "mode" }
        };

        var room = await Pulse.JoinRandomRoom(filter, 0, fallback);
        Debug.Log($"Playing in {room.Name}");
    }
}
```

The filter can only see lobby properties. A key you filter on must be listed in `RoomOptions.LobbyProperties` of the rooms you want to find, as `mode` is above.

## Leaving and rejoining

```csharp
await Pulse.LeaveRoom();
```

`LeaveRoom()` returns to `Connected`. All network objects of the room are destroyed locally and `OnLeftRoom` fires. Calling it outside a room fails with `InvalidState`.

`LeaveRoom(becomeInactive: true)` and a lost connection leave your slot reserved instead of removing you, if the room was created with `PlayerTtlMs` other than `0`. A positive value keeps the slot for that many milliseconds. Other clients get `OnPlayerLeftRoom` with `player.IsInactive` set to `true`, and your objects stay in the room. When the same `UserId` calls `JoinRoom` for that room before the time runs out, the server gives the old `ActorId` back, and the other clients get `OnPlayerEnteredRoom` again. With `PlayerTtlMs` at `0`, leaving always removes the player, and the objects they own are destroyed for everyone.

For rejoin to work, the `UserId` must stay the same, so keep `Persist User Id` on ([connecting](connecting.md#identity)).

## The room you are in

Once joined, these are available from `Pulse`:

| Member | Meaning |
|---|---|
| `Pulse.IsInRoom` | True in the `InRoom` state |
| `Pulse.CurrentRoom` | The `Room`, or `null` |
| `Pulse.LocalPlayer`, `Pulse.LocalActorId` | You |
| `Pulse.IsMasterClient` | Whether you are the master |

`Room` and `Player` are covered in [rooms and properties](rooms-and-properties.md). To wait until enough players have arrived, use `Pulse.WaitForPlayersAsync` ([async](async.md)).
