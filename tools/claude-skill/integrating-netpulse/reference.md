# NetPulse API reference for game integration

Namespaces: `using NetPulse;` for the client API, `using NetPulse.Common;` for `RoomOptions`, `PropertyTable`, `ReturnCode`, `DeliveryMode`.

## Connection and rooms (`Pulse`, static)

| Member | Use |
|---|---|
| `Pulse.NickName`, `Pulse.AuthToken` | Set before connecting |
| `NetOperation Pulse.Connect()` | Uses `NetPulseSettings` from Resources; overloads take `NetPulseSettings` or `ConnectionOptions` |
| `NetOperation<Room> Pulse.JoinOrCreateRoom(name, RoomOptions options = null)` | Also `CreateRoom(name, options)` and `JoinRoom(name)` |
| `NetOperation<Room> Pulse.JoinRandomRoom(PropertyTable expectedProperties = null, byte expectedMaxPlayers = 0, RoomOptions createOptions = null)` | Joins any open room matching the filters (`0` = any size). With `createOptions` the server creates a room with a generated name when nothing matches; without it the operation fails with `ReturnCode.NoMatchFound` |
| `Pulse.JoinLobby()`, `Pulse.LobbyRooms`, `Events.OnRoomListUpdate` | Room browser |
| `Pulse.LeaveRoom()`, `Pulse.Disconnect()` | |
| `Pulse.State`, `IsConnected`, `IsInRoom`, `IsMasterClient` | |
| `Pulse.CurrentRoom`, `LocalPlayer`, `LocalActorId` | |
| `Pulse.ServerTime`, `Tick`, `TickRate`, `TickDelta`, `RoundTripTimeMs` | Shared clock |
| `Pulse.WaitForPlayersAsync(count)`, `WaitUntilAsync(Func<bool>)` | Await helpers |
| `Pulse.LoadScene(name)` | Master only; everyone follows (`AutoSyncScene`) |
| `Pulse.OfflineMode = true` | Same API without a server |

`RoomOptions`: `MaxPlayers` (a `byte`, cast `int` values), `IsVisible`, `IsOpen`, `CustomProperties`, `LobbyProperties`, `Plugin`, `PlayerTtlMs`, `EmptyRoomTtlMs`.

Every `NetOperation` supports `await`, `yield return` and `.Completed`. Awaiting a failed operation throws `NetPulseException` (`ReturnCode`, `Message`). Async code resumes on the main thread.

## Events (`Pulse.Events`)

`OnConnected`, `OnDisconnected(string)`, `OnJoinedRoom`, `OnLeftRoom`, `OnJoinRoomFailed(short, string)`, `OnPlayerEnteredRoom(Player)`, `OnPlayerLeftRoom(Player)`, `OnMasterClientSwitched(Player)`, `OnRoomPropertiesChanged(PropertyTable)`, `OnPlayerPropertiesChanged(Player, PropertyTable)`, `OnOwnershipChanged(NetworkObject, Player, Player)`, `OnOwnershipRequest(NetworkObject, Player)`, `OnNetworkSceneLoaded(string)`. Unsubscribe in `OnDisable`/`OnDestroy`. A `MonoBehaviour` can instead derive from `NetPulseCallbacks` and override the same names.

## Spawning

`Pulse.Instantiate(GameObject prefab, Vector3 pos, Quaternion rot, object data = null)` returns the local instance. The prefab needs `NetworkObject` on its root and must be under `Assets` (auto-registered in `Assets/Resources/NetworkPrefabRegistry.asset`; rebuild with `NetPulse > Rebuild Prefab Registry`). `data` arrives as `NetworkObject.InstantiationData`. Destroy with `Pulse.Destroy(gameObject)` (owner or master). Objects placed in a scene (doors, pickups) need `NetworkObject` with *Scene Object* set; they are owned by the master until someone takes them, and the master registers them automatically after joining and after `LoadScene`. Set Ownership Mode `Takeover` for objects any player may grab, then `await NetworkObject.RequestOwnershipAsync()` before writing their `SyncVar`s.

`NetworkObject` inspector: Ownership Mode (`Fixed`, `Takeover`, `Request`), Interest Group, Persist Across Scenes.

## NetworkBehaviour

Properties: `NetworkObject`, `IsMine`, `Owner`, `ViewId`, `IsSpawned`.

Overridable hooks: `OnNetworkSpawned()` (initial state is applied), `OnNetworkDespawned()`, `OnOwnerChanged(Player previous, Player current)`, `OnOwnershipGained()`, `OnOwnershipLost()`, `OnMasterChanged(Player, Player)`, `OnNetworkTick(long tick)`, `OnOwnershipRequest(Player requester)`, `OnSerialize(NetStream)`.

Ownership: `NetworkObject.RequestOwnership()`, `TransferOwnership(player)`, `ApproveOwnership(requester)`, `RequestOwnershipAsync()` returning `NetOperation<bool>`. Changes apply only after the server confirms.

## RPC (generated; the class must be `partial`)

```csharp
[NetRpc]                               // default target: All
private void Jump(float height) { }

[NetRpc(RpcTarget.Others, Delivery = DeliveryMode.UnreliableSequenced, OwnerOnly = true)]
private void Aim(Vector3 point, NetRpcInfo info) { }   // NetRpcInfo is optional and last: Sender, SenderActorId, ServerTime

Rpc.Jump(2f);
Rpc.To(RpcTarget.Owner).Jump(2f);
Rpc.To(player).Jump(2f);
```

`RpcTarget`: `All`, `Others`, `MasterClient`, `Owner`, `AllBuffered`, `OthersBuffered` (buffered ones replay for late joiners). `Rpc` is protected (private in sealed classes): call it inside the behaviour and expose a public method for other scripts. Supported parameter types: primitives, `string`, enums, Unity vectors/`Quaternion`/`Color`/`Rect`, `Player`, `NetworkObject`, arrays and `List<T>` of those, and `[NetSerializable]` types. No overloads, no return values, no `ref`/`out`.

## Synchronized state

```csharp
private readonly SyncVar<int> _health = new(100);                          // owner writes
private readonly SyncVar<string> _title = new("", SyncPermission.Master); // master writes
_health.Value = 50;
_health.Changed += (previous, current) => { };
```

Writes without permission are ignored with one warning. Late joiners get the current values before `OnNetworkSpawned`. Old-style alternatives: `[Synced]` fields and `OnSerialize(NetStream stream)` with `stream.IsWriting`.

Movement: `NetworkTransform` (position, rotation, scale, interpolation, extrapolation), `NetworkRigidbody`, `NetworkAnimator` (parameters plus `SetTrigger`).

## Room and player properties

```csharp
private static readonly RoomKey<int> Score = new("score");
private static readonly PlayerKey<string> Team = new("team");

room.Get(Score);
room.Set(Score, 5);                                   // applied locally at once, rolled back if the server refuses (0.3.1+)
await room.SetAsync(Score, 6, 5);                     // compare-and-set, throws PropertyCasFailed on conflict
IDisposable sub = room.OnChanged(Score, value => { });
Pulse.LocalPlayer.Set(Team, "red");
IDisposable sub2 = player.OnChanged(Team, (who, value) => { });
```

## Messages (not tied to an object)

```csharp
public partial struct ChatMessage : INetMessage
{
    public string Text;
}

Pulse.Send(new ChatMessage { Text = "gg" }, RpcTarget.Others);
IDisposable sub = Pulse.On<ChatMessage>((message, sender) => { });
```

`[NetSerializable] public partial struct|class T` makes a custom type usable in RPCs, messages and properties.

## Analyzer ids

NP0001 missing `partial`; NP0002 `[NetRpc]` outside a `NetworkBehaviour`; NP0003 unsupported RPC parameter; NP0004 RPC returns a value or has `ref`/`out`/`in`; NP0005 overloaded RPC; NP0006 unsupported field in a serializable type; NP0007 unsupported `SyncVar<T>`; NP0008 `SyncVar` field not readonly; NP0009 id collision; NP0010 unsupported `[Synced]` field; NP0011 `CallRpc("name")` where `Rpc.Name()` exists.

## Server

The server is a .NET 10 app. From the NetPulse server repository run `dotnet run --project src/NetPulse.Server`; it listens on UDP 5055 and reads `appsettings.json` (`NetPulse:Port`, `AppId`, plugins). For production use `dotnet publish` and run the output as a service. Open the UDP port in the firewall.

## Worked example (compiles against NetPulse 0.3)

A shooter where each player owns their character, damage goes to the victim's owner, and the room keeps a shared score. Prefab: `NetworkObject`, `NetworkTransform` (on 0.3.0 set Interpolation Delay to 0.1), `CharacterController`, `PlayerController`, `Health`, `Gun`.

### PlayerController.cs

```csharp
using NetPulse;
using UnityEngine;

[RequireComponent(typeof(CharacterController), typeof(NetworkObject))]
public sealed class PlayerController : NetworkBehaviour
{
    [SerializeField] private float _speed = 5f;
    [SerializeField] private Camera _camera;

    private CharacterController _controller;

    private void Awake() => _controller = GetComponent<CharacterController>();

    public override void OnNetworkSpawned()
    {
        _controller.enabled = IsMine;
        if (_camera != null)
        {
            _camera.gameObject.SetActive(IsMine);
        }
    }

    private void Update()
    {
        if (!IsSpawned || !IsMine)
        {
            return;
        }

        var input = new Vector3(Input.GetAxis("Horizontal"), 0f, Input.GetAxis("Vertical"));
        _controller.SimpleMove(input * _speed);
    }
}
```

### Health.cs

```csharp
using System;
using NetPulse;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public sealed partial class Health : NetworkBehaviour
{
    [SerializeField] private int _max = 100;

    private readonly SyncVar<int> _current = new(100);

    public event Action<int> Changed;
    public event Action<Player> Died;

    public int Current => _current.Value;

    public override void OnNetworkSpawned()
    {
        if (IsMine)
        {
            _current.Value = _max;
        }

        _current.Changed += (_, value) => Changed?.Invoke(value);
    }

    public void Hit(int damage) => Rpc.To(RpcTarget.Owner).TakeDamage(damage);

    [NetRpc]
    private void TakeDamage(int damage, NetRpcInfo info)
    {
        if (_current.Value <= 0)
        {
            return;
        }

        _current.Value = Mathf.Max(0, _current.Value - damage);
        if (_current.Value == 0)
        {
            Rpc.To(RpcTarget.All).AnnounceDeath(info.SenderActorId);
        }
    }

    [NetRpc]
    private void AnnounceDeath(int killerActorId) => Died?.Invoke(Pulse.CurrentRoom?.GetPlayer(killerActorId));
}
```

### Gun.cs

```csharp
using NetPulse;
using UnityEngine;

public sealed partial class Gun : NetworkBehaviour
{
    [SerializeField] private int _damage = 10;
    [SerializeField] private Transform _muzzle;
    [SerializeField] private ParticleSystem _flash;

    private void Update()
    {
        if (!IsSpawned || !IsMine || !Input.GetMouseButtonDown(0))
        {
            return;
        }

        Rpc.To(RpcTarget.All).Flash();
        if (!Physics.Raycast(_muzzle.position, _muzzle.forward, out var hit, 100f))
        {
            return;
        }

        var health = hit.collider.GetComponentInParent<Health>();
        if (health != null && !health.IsMine)
        {
            health.Hit(_damage);
        }
    }

    [NetRpc]
    private void Flash() => _flash.Play();
}
```

### GameManager.cs

```csharp
using System;
using NetPulse;
using NetPulse.Common;
using UnityEngine;
using UnityEngine.UI;

public sealed class GameManager : MonoBehaviour
{
    private static readonly RoomKey<int> Score = new("score");

    [SerializeField] private GameObject _playerPrefab;
    [SerializeField] private Transform[] _spawnPoints;
    [SerializeField] private Text _scoreLabel;
    [SerializeField] private string _roomName = "main";

    private IDisposable _scoreSubscription;

    private async void Start()
    {
        try
        {
            await Pulse.Connect();
            var room = await Pulse.JoinOrCreateRoom(_roomName, new RoomOptions { MaxPlayers = 16 });
            _scoreSubscription = room.OnChanged(Score, ShowScore);
            ShowScore(room.Get(Score));
            SpawnPlayer();
        }
        catch (NetPulseException e)
        {
            Debug.LogError($"NetPulse join failed: {e.ReturnCode} {e.Message}");
        }
    }

    private void OnDestroy() => _scoreSubscription?.Dispose();

    public void AddScore(int amount) => AddScoreAsync(amount);

    private async void AddScoreAsync(int amount)
    {
        var room = Pulse.CurrentRoom;
        for (var attempt = 0; attempt < 5 && room != null; attempt++)
        {
            var current = room.Get(Score);
            try
            {
                await room.SetAsync(Score, current + amount, current);
                return;
            }
            catch (NetPulseException e) when (e.ReturnCode == ReturnCode.PropertyCasFailed)
            {
            }
        }
    }

    private void SpawnPlayer()
    {
        var point = _spawnPoints[UnityEngine.Random.Range(0, _spawnPoints.Length)];
        Pulse.Instantiate(_playerPrefab, point.position, point.rotation);
    }

    private void ShowScore(int value) => _scoreLabel.text = value.ToString();
}
```

To credit kills, subscribe to `Health.Died` and call `GameManager.AddScore(1)` only when the killer is the local player (`killer != null && killer.IsLocal`), so one kill is counted once.
