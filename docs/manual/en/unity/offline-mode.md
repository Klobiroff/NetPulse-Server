# Offline mode

Offline mode runs the same NetPulse API against a local, in-memory room. There is no server and no socket. It lets a single-player mode, a tutorial or a test use exactly the code that the online game uses.

Switch it on before connecting, while the state is `Disconnected`:

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class StartGame : MonoBehaviour
{
    [SerializeField] private bool _singlePlayer;
    [SerializeField] private GameObject _playerPrefab;

    private async void Start()
    {
        Pulse.OfflineMode = _singlePlayer;
        await Pulse.Connect();
        await Pulse.JoinOrCreateRoom("main", new RoomOptions { MaxPlayers = 4 });
        Pulse.Instantiate(_playerPrefab, Vector3.zero, Quaternion.identity);
    }
}
```

Changing `OfflineMode` in any other state logs an error and does nothing. To switch, call `Pulse.Disconnect()` first.

## What the API does offline

| Area | Behavior |
|---|---|
| `Connect()` | Succeeds at once and raises `OnConnected`. The user id comes from the settings as usual. |
| Lobby | `JoinLobby()` moves to `InLobby`. `GetRoomList()` returns an empty list. |
| Rooms | `CreateRoom`, `JoinRoom`, `JoinOrCreateRoom` and `JoinRandomRoom` all create a local room (named `OfflineRoom` if you pass no name). The local player is actor 1 and the master. The `RoomOptions` limits and `CustomProperties` are applied. `OnCreatedRoom` and `OnJoinedRoom` fire. |
| Properties | Applied locally and immediately. A compare-and-set does not compare: the new value is written. |
| RPCs, messages, `RaiseEvent` | Delivered to your own handlers when the target includes you: `All`, `AllBuffered`, `MasterClient`, `Owner` or your own `Player`. `Others` and `OthersBuffered` reach nobody. |
| Objects | `Pulse.Instantiate` creates the object locally, and the prefab registry is not needed. Owner state is never sent, so `OnSerialize` is not called. `SyncVar<T>` still raises `Changed` for local writes. |
| Ownership | `RequestOwnership()` makes you the owner at once. `Fixed` objects still refuse. |
| Time and ticks | `ServerTime` is the local clock. The tick rate is 30, and `OnNetworkTick` runs as usual. |
| Scenes | `Pulse.LoadScene` works, because you are the master. |
| Stats | `RoundTripTimeMs` is `0` and no traffic is counted. |

The code path is the same as online, so bugs in game logic show up offline too. What offline cannot show you is anything that depends on a second player or on the network: ordering between clients, ownership contention, interpolation of remote objects.

## Tips

- Keep the online and offline paths down to one `if` at start-up, as above. Everything after `Connect()` can stay the same.
- A game that offers both modes can let the player choose, then call `Disconnect()` and flip `OfflineMode` when they return to the menu.
- Because ownership always succeeds offline, test the `Request` and `Fixed` rules against a real server as well.
