# Rooms and properties

Once you are in a room, `Pulse.CurrentRoom` is a `Room` and every member, including you, is a `Player`. Both can carry properties: small values that the server stores, that every member can read, and that late joiners receive when they join.

## Room and Player

`Room` members:

| Member | Meaning |
|---|---|
| `Name`, `MaxPlayers`, `IsOpen`, `IsVisible` | The room's settings (see [matchmaking](matchmaking.md#roomoptions)) |
| `MasterClientId`, `MasterClient` | The current master |
| `LocalPlayer` | You |
| `Players`, `PlayerCount` | All players, ordered by `ActorId`; inactive players are included |
| `GetPlayer(actorId)`, `TryGetPlayer(actorId, out player)` | Look up by actor id |
| `GetNextPlayer(after)` | The player with the next higher `ActorId`, wrapping around to the first |
| `Properties` | The raw `PropertyTable` |

`Player` members: `ActorId`, `NickName`, `UserId`, `IsLocal`, `IsInactive`, `IsMasterClient` and `Properties`.

```csharp
using NetPulse;
using UnityEngine;

public sealed class RoomPrinter : MonoBehaviour
{
    private void Print()
    {
        var room = Pulse.CurrentRoom;
        if (room == null)
        {
            return;
        }

        foreach (var player in room.Players)
        {
            var tags = (player.IsMasterClient ? " master" : string.Empty) + (player.IsLocal ? " you" : string.Empty);
            Debug.Log($"#{player.ActorId} {player.NickName}{tags}");
        }
    }
}
```

## Typed keys

A `RoomKey<T>` or `PlayerKey<T>` ties a property name to a value type, so the compiler checks both. Declare each key once as a `static readonly` field and use it everywhere.

```csharp
using System;
using NetPulse;
using UnityEngine;

public sealed class Scores : MonoBehaviour
{
    private static readonly RoomKey<int> Score = new("score");
    private static readonly PlayerKey<string> Mood = new("mood");

    private IDisposable _scoreWatch;
    private IDisposable _moodWatch;

    private void OnEnable() => Pulse.Events.OnJoinedRoom += Subscribe;

    private void OnDisable()
    {
        Pulse.Events.OnJoinedRoom -= Subscribe;
        _scoreWatch?.Dispose();
        _moodWatch?.Dispose();
    }

    public void AddPoint()
    {
        var room = Pulse.CurrentRoom;
        room.Set(Score, room.Get(Score) + 1);
    }

    private void Subscribe()
    {
        var room = Pulse.CurrentRoom;
        _scoreWatch = room.OnChanged(Score, value => Debug.Log($"Score is {value}"));
        _moodWatch = Pulse.LocalPlayer.OnChanged(Mood, (player, value) => Debug.Log($"{player.NickName} feels {value}"));
        Pulse.LocalPlayer.Set(Mood, "happy");
    }
}
```

The API on `Room` and `Player` is the same shape:

| Call | Meaning |
|---|---|
| `Get(key, defaultValue)` | Reads the local copy. Returns `defaultValue` (or `default`) if the key is missing or not convertible. |
| `Set(key, value)` | Writes. The local copy and the `OnChanged` handlers update at once, and the change goes to the server and the other clients. |
| `SetAsync(key, value, expected)` | Compare-and-set. Returns a `NetOperation`. See below. |
| `OnChanged(key, handler)` | Subscribes to changes of one key. Returns an `IDisposable` that unsubscribes. |

For a `PlayerKey<T>`, the handler receives the `Player` and the new value. `OnChanged` fires for your own writes too, and for the other clients' changes when they arrive. It fires for a key only when that key is part of the change.

Numbers convert between numeric types, so a key declared as `RoomKey<int>` reads a value another client stored as a `long`. A key that is missing reads as the default.

### Who may write

Any member can write room properties and its own player properties. Writing another player's properties is allowed only to the master client. The reserved room keys `_isOpen`, `_isVisible`, `_maxPlayers` and `_scene` are master-only as well. `Set` applies the change locally before the server has answered and does not report a rejection; if the server refuses it, your local copy keeps the rejected value. When you need to know whether a write was accepted, use the compare-and-set form, which waits for the server.

Writing `null` removes a key. A typed `Set` cannot pass `null` for a value type, so remove with the raw API: `room.SetProperty("score", null)`.

## Compare-and-set

`SetAsync(key, value, expected)` succeeds only if the stored value still equals `expected`. The server checks and applies it atomically, and the local copy changes only after the server accepts. A mismatch fails the operation with `PropertyCasFailed` (-15). This is how several clients can claim a slot or increment a counter without overwriting each other.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class ClaimHost : MonoBehaviour
{
    private static readonly RoomKey<string> Host = new("host");
    private static readonly RoomKey<int> Round = new("round");

    private async void Claim()
    {
        var room = Pulse.CurrentRoom;

        try
        {
            await room.SetAsync(Host, Pulse.LocalPlayer.NickName, expected: null);
            Debug.Log("You are the host");
        }
        catch (NetPulseException e) when (e.ReturnCode == ReturnCode.PropertyCasFailed)
        {
            Debug.Log($"{room.Get(Host)} got there first");
        }
    }

    private async void NextRound()
    {
        var room = Pulse.CurrentRoom;
        var current = room.Get(Round);
        await room.SetAsync(Round, current + 1, current);
    }
}
```

A missing key never equals a number. If `round` was never set, `SetAsync(Round, 1, 0)` fails, because the server compares "absent" with `0`. Create the room with the key in `CustomProperties` so it exists, or use `null` as `expected` through the raw API described next.

## The raw PropertyTable

Typed keys sit on top of string keys and `PropertyTable` (in `NetPulse.Common`). The raw API is useful when keys are built at runtime or when you set several properties in one request.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class RawProperties : MonoBehaviour
{
    private async void Start()
    {
        var room = Pulse.CurrentRoom;

        room.SetProperty("map", "forest");
        var map = room.GetProperty<string>("map", "unknown");

        var delta = new PropertyTable { ["round"] = 2, ["phase"] = "fight" };
        var expected = new PropertyTable { ["round"] = 1 };
        await Pulse.Client.SetRoomProperties(delta, expected);

        Pulse.LocalPlayer.SetProperty("team", 1);
        var team = Pulse.LocalPlayer.GetProperty<int>("team");
        Debug.Log($"{map} team {team} {room.Properties.Count} properties");
    }
}
```

| Call | Meaning |
|---|---|
| `room.SetProperty(key, value)`, `player.SetProperty(key, value)` | Set one key. `null` removes it. |
| `room.SetProperties(delta, expected)`, `player.SetProperties(delta)` | Set several keys in one request. `expected` makes it a compare-and-set; every key in `expected` must match, and a key with a `null` value matches a missing key. |
| `GetProperty<T>(key, defaultValue)` | Read one key. |
| `Pulse.Client.SetRoomProperties(delta, expected)`, `Pulse.Client.SetPlayerProperties(player, delta, expected)` | Same as above but return a `NetOperation` you can await. |
| `Properties` | The `PropertyTable` itself: `Get<T>`, `TryGet<T>`, `ContainsKey`, `Remove`, `Keys`, `Count`, `Merge`, `Matches`, `Clone`. |

Property values may be `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `string`, `Guid`, arrays of `byte`, `int`, `float`, `string` or `object`, a nested `PropertyTable`, the Unity types `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int` and `Vector3Int`, and types marked `[NetSerializable]`. The [messages](messages.md) page lists what a `[NetSerializable]` type can contain. Any other type throws a serialization exception when sent.

## Change notifications

Besides `OnChanged`, you can listen to every change with `Pulse.Events.OnRoomPropertiesChanged` and `OnPlayerPropertiesChanged` (or the `IRoomCallbacks` interface, see [connecting](connecting.md#states-and-events)). They deliver only the changed keys as a `PropertyTable`, not the whole table. Your own writes raise these events immediately; a compare-and-set raises them once the server has accepted it.
