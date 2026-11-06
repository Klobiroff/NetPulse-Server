# Settings and editor

This page lists every field of `NetPulseSettings`, then the editor windows and menu items that NetPulse adds.

## NetPulseSettings

The settings are a `ScriptableObject` asset. The client loads it at runtime with `Resources.Load("NetPulseSettings")`, so it has to sit in a `Resources` folder. The default location is `Assets/Resources/NetPulseSettings.asset`. If no asset exists, the client uses a temporary instance with the defaults below. To use a different asset in code, pass it to `Pulse.Connect(settings)` ([connecting](connecting.md#connection-settings)).

Create the asset with `NetPulse > Create Settings Asset`, with the button in `Project Settings > NetPulse`, or with `Assets > Create > NetPulse > Settings`.

### Server

| Field | Default | Meaning |
|---|---|---|
| Host | `127.0.0.1` | Server address (IP or host name) |
| Port | `5055` | Server UDP port |
| App Id | empty | Identifies your game. If the server is configured with an `AppId`, the two must match. |
| App Version | `1.0` | Your build's version string. It is sent to the server, which does not check it. |
| Connection Key | `NetPulse` | Shared secret of the UDP handshake. It has to equal the server's `ConnectionKey`. |

### Player

| Field | Default | Meaning |
|---|---|---|
| Default Nick Name | empty | Used when `Pulse.NickName` was not set. If it is empty too, the client picks `Player` plus a random number. |
| Persist User Id | on | Keep the generated `UserId` in `PlayerPrefs`. Needed for [rejoining](matchmaking.md#leaving-and-rejoining). |

### Rates

| Field | Default | Meaning |
|---|---|---|
| Send Rate | `20` (1 to 60) | Upper limit for state sends per second |
| Serialization Rate | `0` (0 to 60) | Second limit for state sends. `0` means the server tick rate. The effective rate is the lower of the two. |
| Request Timeout | `10` (at least 1) | Seconds before a request fails with `Timeout` |

How the two rates turn into a send interval is shown in [ticks and time](ticks-and-time.md#how-ticks-drive-sending).

### Behaviour

| Field | Default | Meaning |
|---|---|---|
| Run In Background | on | `Pulse.Connect` turns on `Application.runInBackground` so the connection stays alive without focus |
| Auto Sync Scene | on | Let `Pulse.LoadScene` load the scene on every client ([scenes](scenes.md)) |
| Collect Stats | `Auto` | `Auto` collects traffic statistics in the editor and development builds only. `Always` and `Never` override that. |
| Log Verbose | off | Reserved. The current client does not read it. |
| Prefab Registry | none | The `NetworkPrefabRegistry` asset used for spawning. Created and filled automatically. |

In code the same values are properties of `NetPulseSettings`, for example `settings.SendRate = 30`. `CollectStats` is a `bool` property there: reading it applies the `Auto` rule, and setting it chooses `Always` or `Never`.

## Project Settings

`Edit > Project Settings > NetPulse` draws all fields of the asset. It has two buttons: `Rebuild Prefab Registry` and `Open Hub`. Without an asset it shows a notice and a `Create Settings Asset` button.

## The NetPulse Hub

`NetPulse > Open Hub` opens a window with two tabs. It updates four times a second while the game runs.

The `Overview` tab shows:

- Connection: state, online or offline mode, ping, server time, user id and nickname. In Play Mode there are `Connect` and `Disconnect` buttons.
- Room: name, player count against the limit, the open and visible flags, the number of network objects and the player list, with markers for yourself, the master and inactive players.
- Traffic: messages and kilobytes sent and received, with the current kilobytes per second.
- Prefab registry: how many prefabs are registered, and a `Rebuild Prefab Registry` button.

### Traffic tab

The `Traffic` tab is a profiler for the traffic of the running client. It needs Play Mode and `Collect Stats` on (the `Collect` toggle at the top switches it live). `Reset` clears the counters. The top line shows the current bytes per second, and the second line the session totals.

The table has one row per entry, with columns `Category`, `Name`, `In B/s`, `Out B/s`, `In total`, `Out total` and `Msgs in/out`. Click a column header to sort by it, and click again to reverse the order. The popup and the text box above the table filter by category and by name.

Categories:

| Category | One row per |
|---|---|
| `NetworkObject` | Network object (`viewId name`): its spawn, destroy, state and RPC traffic |
| `Rpc` | RPC method (`Type.Method`) |
| `Message` | Message type |
| `StateSync` | Object state updates (a single row) |
| `SyncVar` | Behaviour type that sends `Master` variables |
| `System` | Protocol traffic: `Request op N`, `Response op N`, `Event N`, `Ping`, `Pong` |

The categories overlap on purpose. An RPC counts under `Rpc` and also under the `NetworkObject` it belongs to, so do not add the rows together. The rate columns use a window of one second.

You can read the same data in code:

```csharp
using NetPulse;
using UnityEngine;

public sealed class TrafficLog : MonoBehaviour
{
    private void Dump()
    {
        var stats = Pulse.Stats;
        Debug.Log($"in {stats.BytesInPerSecond:F0} B/s, out {stats.BytesOutPerSecond:F0} B/s, session {stats.TotalBytesIn + stats.TotalBytesOut} B");

        foreach (var entry in stats.Entries)
        {
            if (entry.Category == StatCategory.Rpc)
            {
                Debug.Log($"{entry.Name}: {entry.MessagesOut} sent, {entry.BytesOut} B");
            }
        }
    }
}
```

`NetStats` exposes `Enabled`, `Entries` (each `NetStatEntry` has `Category`, `Name`, `BytesIn`, `BytesOut`, `MessagesIn`, `MessagesOut`, `BytesInPerSecond` and `BytesOutPerSecond`), the `Total...` counters, `BytesInPerSecond`, `BytesOutPerSecond` and `Reset()`. When collection is off, the counters cost nothing.

## NetworkObject inspector

The inspector for `NetworkObject` shows `Ownership Mode`, `Interest Group`, `Persist Across Scenes` and the `Scene Object` toggle with its `Scene Id` (an error appears outside 1 to 999). The scene object toggle is disabled on prefab assets and when several objects are selected. On a prefab asset a notice reminds you that it is added to the registry automatically. In Play Mode a read-only `Runtime` block shows `View Id`, `Owner Actor`, `Is Mine` and `Spawned`.

## Menu items

| Menu | What it does |
|---|---|
| `NetPulse > Create Settings Asset` | Creates or selects the settings asset |
| `NetPulse > Open Settings` | Opens `Project Settings > NetPulse` |
| `NetPulse > Open Hub` | Opens the Hub window |
| `NetPulse > Rebuild Prefab Registry` | Scans `Assets` for prefabs with a `NetworkObject` and updates the registry |
| `NetPulse > Tools > Preview IL2CPP link.xml` | Writes and shows the generated `link.xml` ([IL2CPP](il2cpp.md)) |
| `NetPulse > Samples > Build Basic Sample Scene` | Builds the Basic sample scenes (import the sample first, [installation](installation.md#the-basic-sample)) |
| `Assets > Create > NetPulse > Settings` and `Network Prefab Registry` | Create the two assets by hand |
| `Add Component > NetPulse` | `Network Transform`, `Network Rigidbody`, `Network Animator` |

The registry also rebuilds itself when prefabs under `Assets` are imported, moved or deleted, and before each build.
