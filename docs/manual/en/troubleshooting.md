# Troubleshooting

Client and connection problems, grouped by symptom. For problems on the server side (logs, limits, plugins, deployment), see [operations](server/operations.md). The Unity console is the first place to look: NetPulse prefixes its messages with `[NetPulse]`.

## Connecting

### Connect fails with ConnectionFailed, Timeout or HostUnreachable

The client got no answer from the server. Check, in this order:

1. The server is running and listens on UDP (not TCP). The default port is 5055.
2. `Host` and `Port` in `NetPulseSettings` are right. `127.0.0.1` only reaches a server on the same machine.
3. A firewall or cloud security group allows inbound UDP on that port.
4. The `Connection Key` equals the server's `ConnectionKey`. A mismatch is reported as `ConnectionRejected`.

### Connect fails with InvalidAppId, VersionMismatch or AuthenticationFailed

| Code | Cause | Fix |
|---|---|---|
| `InvalidAppId` (-31) | The server has an `AppId` configured and yours differs | Set the same `App Id` in the settings |
| `VersionMismatch` (-30) | Client and server speak different protocol versions | Update the package or the server so both are 0.3 |
| `AuthenticationFailed` (-32) | The auth plugin rejected the token | Set `Pulse.AuthToken` before `Connect()`, and check the plugin ([authentication](plugins/authentication.md)) |

### "Cannot connect while ..." or InvalidState

`Connect()` works only in the `Disconnected` state, and joins work only from `Connected` or `InLobby`. Wait for the earlier operation, or call `Pulse.Disconnect()` first. `Pulse.State` shows where the client is.

### The connection drops when the game freezes or loses focus

The transport gives up after 10 seconds without packets. Typical causes are a stalled main thread (a long blocking call or a breakpoint) and `Application.runInBackground` being off while the window is unfocused. Leave `Run In Background` on in the settings.

### A second client in the same project cannot join: "User is already in this room"

Both instances read the same `UserId` from `PlayerPrefs`, and the server allows one player per `UserId` in a room. Clones made by Multiplayer Play Mode or by copying the project can share `PlayerPrefs`. Turn off `Persist User Id` for the test client, or set a different `ConnectionOptions.UserId` per instance.

### Requests time out

A request that gets no answer within `Request Timeout` (10 seconds by default) fails with `Timeout` (-100). Look for packet loss, a server that is overloaded, or an exception in a server plugin. Raising the timeout hides the cause and only helps on very slow links.

## Matchmaking

| Symptom | Cause and fix |
|---|---|
| `RoomNotFound` | The name is wrong, or the room already closed. Rooms close when empty unless `EmptyRoomTtlMs` is set. |
| `RoomFull`, `RoomClosed` | `MaxPlayers` reached, or the master set `_isOpen` to false |
| `RoomAlreadyExists` | `CreateRoom` with a taken name. Use `JoinOrCreateRoom`. |
| `NoMatchFound` from `JoinRandomRoom` | Nothing visible, open and not full matches. Pass `createOptions` to create a room instead. |
| `JoinRandomRoom` finds nothing though rooms exist | The filter works on lobby properties only. List the key in `RoomOptions.LobbyProperties` when you create the room. |
| The room list is empty | You have to be in the lobby (`JoinLobby()` from `Connected`). The list shows visible rooms only, and updates arrive about once a second. After leaving a room, join the lobby again. |
| Rejoin gives a new `ActorId` | The room was created with `PlayerTtlMs` at 0, the slot expired, or the `UserId` changed ([matchmaking](unity/matchmaking.md#leaving-and-rejoining)) |

## Objects and spawning

| Message or symptom | Cause and fix |
|---|---|
| `Prefab '...' is not in the NetworkPrefabRegistry` | The prefab has no `NetworkObject` on its root, is outside the `Assets` folder, or the registry is stale. Use `NetPulse > Rebuild Prefab Registry`. |
| `Received spawn for unknown prefab id` | The sending client has a prefab the receiver's registry lacks. Build both from the same project state. |
| `Instantiate requires a joined room` | Wait for `JoinRoom` to complete before spawning |
| `Prefab '...' has no NetworkObject component on its root` | Add the component to the root, not to a child |
| An object exists only on my machine | It was created with `Object.Instantiate` instead of `Pulse.Instantiate`, or destroyed with `Object.Destroy` on another client |
| `Only the owner or the master client can destroy` | Ask the owner or the master to destroy it, for example with an RPC |
| Scene objects have wrong owners | Scene ids must be unique and the master registers them after joining. See [network objects](unity/network-objects.md#scene-objects). |

## RPCs and state

### The name Rpc does not exist, or a compile error mentions NP00xx

The class is not `partial`, or the source generator is not active for the assembly. Check:

- the class and every type that contains it are `partial` ([analyzers](unity/analyzers.md));
- an assembly definition that holds the code references `NetPulse.Runtime` and `NetPulse.Common`;
- `Runtime/Analyzers/NetPulse.Generators.dll` kept its `.meta` file (it carries the `RoslynAnalyzer` label). If you copied the package by hand, copy the `.meta` files too.

### An RPC logs "requires a spawned object in a joined room"

The call came too early (in `Awake` or `OnEnable`) or outside a room. Call RPCs from `OnNetworkSpawned` or later ([lifecycle](unity/lifecycle.md)).

### "Received an RPC (id ...) that ... does not define"

The sender runs a different version of the project, or the receiver's class is not `partial` and the generator is inactive there. Both sides need the same RPC names.

### OwnerOnly RPC was not sent

The calling client does not own the object. The check is local: ask the owner or change who calls.

### A SyncVar write is ignored

The log says "ignored a write to SyncVar". Only the owner may write an `Owner` variable and only the master may write a `Master` variable. Check who owns the object, or change the permission ([state sync](unity/state-sync.md#permissions)). The warning appears once per field.

### A remote value or object never updates

- The owner sends state only for spawned objects, and only on send ticks. Look at `Send Rate` and `Serialization Rate` ([ticks and time](unity/ticks-and-time.md#how-ticks-drive-sending)).
- A list changed in place does not count as a change. Assign a new list to a `SyncVar<List<T>>`.
- `[Synced]` fields raise no event. Poll them, or use `SyncVar<T>`.
- The server drops state from a client that does not own the object. The log shows a rejected-message warning.

### Remote objects move with a long delay

`NetworkTransform` and `NetworkRigidbody` compute their automatic interpolation delay from `Serialization Rate`. With the default of `0` the delay comes to 2 seconds. Set `Interpolation Delay` on the component to a small value such as 0.1 to 0.15 seconds ([components](unity/components.md)).

## Ownership

| Symptom | Cause and fix |
|---|---|
| `RequestOwnership()` does nothing | The mode is `Fixed` (a warning is logged). Use `Takeover` or `Request`. |
| A request in `Request` mode never succeeds | The owner has to call `ApproveOwnership` in `OnOwnershipRequest`. The server drops the request after its timeout (5 seconds by default). |
| `OnOwnershipFailed` with `Forbidden` | The mode forbids the change, or a plugin vetoed it. Your local state is unchanged. |
| Objects stay `Fixed` and never change owner | The server is older than the client and predates ownership enforcement (before 0.2). Update the server. |
| `IsMine` is still false right after `RequestOwnership()` | It changes when the server's event arrives. Use `RequestOwnershipAsync` or wait for `OnOwnerChanged`. |

## Scenes

- "The network scene could not be loaded. Is it added to Build Settings?" Add the scene to `File > Build Settings`.
- `LoadScene` warns "Only the master client can load a network scene". Send the master an RPC or a message and let it call `LoadScene`.
- Nothing loads on other clients. `Auto Sync Scene` is off in the settings, or those clients are not in the room.
- Objects disappear after a load. Only objects with `Persist Across Scenes` survive, as roots ([scenes](unity/scenes.md)).
- RPCs seem to arrive late after a load. Events are held during the load and replayed in order afterwards.

## Messages and callbacks

- "... is not registered as a message": the type must be `partial` and implement `INetMessage`, and it has to exist in the build.
- "Received a message with unknown id": the sender uses a type the receiver lacks, usually a version mismatch or a renamed type.
- A handler never fires: check that you subscribed with `Pulse.On<T>` and kept the returned `IDisposable` alive, and that the sender used a target that includes you. `RpcTarget.Others` does not include the sender.
- Callbacks stop firing in a `NetPulseCallbacks` subclass: if you override `OnEnable` or `OnDisable`, call `base.OnEnable()` and `base.OnDisable()`. `Pulse.UseTransport` resets the client and removes registered targets, so register after it.

## Builds and platforms

- IL2CPP errors about a missing RPC method: run `NetPulse > Tools > Preview IL2CPP link.xml` and check that the type is listed ([IL2CPP](unity/il2cpp.md)).
- WebGL: the default transport uses UDP, which browsers do not offer. Supply your own `INetTransport` (for example over WebSockets) with `Pulse.UseTransport` ([low-level API](unity/low-level-api.md#transport-and-protocol)).
- The Basic sample does not compile: install the `com.unity.inputsystem` package, or set the active input handling to the legacy Input Manager ([installation](unity/installation.md#input-system)).
- The Traffic tab is empty: statistics are off. `Collect Stats` is `Auto`, which means editor and development builds only.
- With domain reload disabled, `Pulse` still resets its static state when play mode starts, including the nickname and the auth token. Set them on every run.
