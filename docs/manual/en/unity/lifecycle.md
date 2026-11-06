# Lifecycle

`NetworkBehaviour` adds a set of virtual hooks to the usual Unity callbacks. This page lists each hook, when it runs and what is guaranteed about the order. Everything runs on the main thread.

## The hooks

| Hook | Runs |
|---|---|
| `OnNetworkSpawned()` | Once, when the object is live in the room |
| `OnNetworkTick(long tick)` | On every network tick while the object is spawned ([ticks and time](ticks-and-time.md)) |
| `OnOwnerChanged(Player previous, Player current)` | When the owner changes |
| `OnOwnershipGained()`, `OnOwnershipLost()` | When you start or stop owning the object |
| `OnOwnershipRequest(Player requester)` | On the owner's client, when someone asks for a `Request` object ([ownership](ownership.md)) |
| `OnMasterChanged(Player previous, Player current)` | When the master client changes |
| `OnSerialize(NetStream stream)` | On each state send (owner) and on each state receive (others) ([state sync](state-sync.md)) |
| `OnNetworkDespawned()` | When the object is destroyed, if `OnNetworkSpawned` had run |

```csharp
using NetPulse;
using UnityEngine;

public sealed class HookProbe : NetworkBehaviour
{
    public override void OnNetworkSpawned() => Debug.Log($"{name}: spawned, mine={IsMine}, view {ViewId}");

    public override void OnNetworkDespawned() => Debug.Log($"{name}: despawned");

    public override void OnOwnerChanged(Player previous, Player current) => Debug.Log($"{name}: owner {previous?.NickName} -> {current?.NickName}");

    public override void OnOwnershipGained() => Debug.Log($"{name}: gained");

    public override void OnOwnershipLost() => Debug.Log($"{name}: lost");

    public override void OnMasterChanged(Player previous, Player current) => Debug.Log($"{name}: master {previous?.NickName} -> {current?.NickName}");
}
```

## Order guarantees

### Creation and spawn

1. Unity runs `Awake` and `OnEnable`. In `NetworkObject.Awake` the object collects its `NetworkBehaviour`s (numbering them in hierarchy order) and registers their `SyncVar`s. Unity does not define the order between `Awake` calls of different components, so do not read `ViewId` or `IsMine` in your own `Awake`. Use `OnNetworkSpawned`.
2. `OnNetworkSpawned` runs exactly once per object, on every behaviour in hierarchy order. For an object you create with `Pulse.Instantiate`, it has already run when `Instantiate` returns. For an object owned by someone else, it normally runs right after the spawn arrives, in the same frame as `Awake` and `OnEnable` and before `Start`.
3. If the object belongs to another player and has state to receive (`SyncVar`, `[Synced]` fields or `OnSerialize`), `OnNetworkSpawned` waits until the first state update has been applied, or one second at most. The first thing your code sees is then real data and not field defaults. In this case `Start` may run before it.
4. `OnNetworkSpawned` always runs before the first `OnNetworkTick` of that object. An object that has not been announced yet gets no ticks.

Scene objects register in `OnEnable`, whether or not you are in a room. If the client is outside a room at that time, `IsMine` is true and `OnNetworkSpawned` runs at once. After you join as a non-master, `IsMine` becomes false without any ownership callback. Do setup that depends on who owns a scene object after `OnJoinedRoom`.

### Ownership change

The server announces the new owner. Then, for each behaviour of the object, in hierarchy order:

1. `OnOwnerChanged(previous, current)`
2. `OnOwnershipGained()` if you became the owner, or `OnOwnershipLost()` if you stopped being one

After all behaviours have been handled, the object raises `NetworkObject.OwnershipChanged`, and then `Pulse.Events.OnOwnershipChanged` (and `IOwnershipCallbacks.OnOwnershipChanged`) fire. The event is raised once for the object, not once per behaviour. Inside every hook, `IsMine` and `Owner` already show the new owner.

An object that a late joiner has only just created and is still waiting for its first state can receive `OnOwnerChanged` before its `OnNetworkSpawned`. Do not assume the spawn hook has run in `OnOwnerChanged`.

### Master change

When the server announces a new master:

1. `OnMasterChanged(previous, current)` is called on every behaviour of every object whose `OnNetworkSpawned` has run. For a scene object without an explicit owner (which the master owns), `OnOwnershipGained()` or `OnOwnershipLost()` follows on the same behaviour if you became or stopped being the master.
2. After all objects, `Pulse.Events.OnMasterClientSwitched(newMaster)` is raised.

### Despawn

`OnNetworkDespawned` is called from the `NetworkObject`'s `OnDestroy`, and only if `OnNetworkSpawned` had run. Unity does not fix the order of `OnDestroy` between components, so a behaviour's own `OnDestroy` can run before or after it. Release network-related resources in `OnNetworkDespawned` and plain resources in `OnDestroy`.

## Where NetPulse runs in the frame

A hidden runner object (`[NetPulse]`, created on first use and kept across scenes) updates the client with execution order -1000, before your scripts' `Update`:

1. It polls the transport. Everything that arrived is dispatched now: spawns, RPCs, state updates, property changes, ownership and master events, and your callbacks.
2. It runs the network tick loop: `OnNetworkTick` for every spawned object, then the state sends of the tick ([ticks and time](ticks-and-time.md)).
3. Your `Update`, `LateUpdate` and coroutines run afterwards, and see the data that arrived in step 1.

While a network scene is loading, room events are queued and dispatched afterwards in the original order ([scenes](scenes.md)).
