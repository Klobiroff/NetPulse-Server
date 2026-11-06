# RPC

A remote procedure call runs a method of a network object on other clients. Mark the method with `[NetRpc]`, then call it through the generated `Rpc` property. The compiler checks the arguments, and nothing is looked up by name at runtime.

## Declaring and calling

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc(RpcTarget.Others)]
    private void Hit(int damage, Vector3 at, NetRpcInfo info) => Debug.Log($"{info.Sender?.NickName} hit for {damage} at {at}");

    [NetRpc(RpcTarget.MasterClient, Delivery = DeliveryMode.Unreliable, OwnerOnly = true)]
    private void Report(string text) => Debug.Log(text);

    public void Attack(Player victim)
    {
        Rpc.Hit(10, transform.position);
        Rpc.To(RpcTarget.All).Hit(10, transform.position);
        Rpc.To(victim).Report("attacked");
    }
}
```

- `Rpc.Hit(10, position)` sends to the target named in the attribute.
- `Rpc.To(RpcTarget.All).Hit(...)` overrides the target for one call.
- `Rpc.To(player).Report(...)` sends to one player.

The attribute is `[NetRpc(RpcTarget target = RpcTarget.All, Delivery = ..., OwnerOnly = ...)]`. Without arguments the target is `All` and the delivery is `ReliableOrdered`.

Requirements:

- The class derives from `NetworkBehaviour` and is `partial`. `sealed partial` works and keeps the generated `Rpc` property private. In a non-sealed class the property is `protected`, so subclasses can use it and outside code cannot. Add a public method like `Attack` above for callers.
- The method returns `void`, has no `ref`, `out` or `in` parameters, is not static, generic or abstract, and has a name that no other method in the class uses. RPC overloads are not allowed, because the RPC id is a hash of the method name.
- A `NetRpcInfo` parameter is optional and must come last. It is filled in by the receiver and is not part of the call.
- The method can have any access level.

The analyzer reports violations as compile errors; see [analyzers](analyzers.md) (NP0001 to NP0005).

## Parameter types

An RPC takes the same types as a [message](messages.md#serializable-types): `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `char`, `string`, `Guid`, enums, the Unity vectors, `Quaternion`, colors and `Rect`, `Player`, `NetworkObject`, `[NetSerializable]` and `INetMessage` types, and arrays or `List<T>` of those. For anything else, pass its fields as separate parameters or mark a type `[NetSerializable]`.

## Targets

| `RpcTarget` | Runs on |
|---|---|
| `All` | Every client, including the caller. The caller's copy runs immediately, inside the call. |
| `Others` | Every client except the caller |
| `MasterClient` | The master. If the caller is the master, it runs locally. |
| `Owner` | The object's owner. If the caller is the owner, it runs locally. For a scene object without an explicit owner, the owner is the master. |
| `AllBuffered` | Like `All`, and the server also stores the call and replays it to clients that join later |
| `OthersBuffered` | Like `Others`, with the same storage |

`Rpc.To(player)` sends to one player. If that player is you, it runs locally.

Buffered RPCs are tied to the object. The server drops the stored calls of an object when the object is destroyed, so a late joiner does not replay RPCs of an object that no longer exists. Use buffering for state that new players must catch up on, such as "door opened", and prefer a [`SyncVar`](state-sync.md) or a property when the value is simply the latest one, because a buffered call is replayed every time it was made.

## Delivery

`Delivery` selects the transport mode: `ReliableOrdered` (default), `ReliableUnordered`, `UnreliableSequenced` or `Unreliable`. An unreliable RPC may be lost, so use it only for repeated, disposable calls such as a cosmetic effect.

## NetRpcInfo and OwnerOnly

A trailing `NetRpcInfo` parameter gives the receiver three values:

| Member | Meaning |
|---|---|
| `Sender` | The `Player` who called. `null` if that player has left. |
| `SenderActorId` | The sender's actor id |
| `ServerTime` | The server time on this client at the moment the call ran |

`OwnerOnly = true` makes the calling client refuse the call unless it owns the object: it logs a warning and sends nothing. The check happens on the caller only. Receivers do not verify that the sender owns the object, so a modified client could still send the call. For anything that matters, validate on the receiving side with `info.Sender` or `info.SenderActorId`, or on the server with a plugin.

```csharp
using NetPulse;
using UnityEngine;

public sealed partial class Door : NetworkBehaviour
{
    [NetRpc(RpcTarget.MasterClient)]
    private void RequestOpen(NetRpcInfo info)
    {
        if (info.Sender == null)
        {
            return;
        }

        Rpc.To(RpcTarget.AllBuffered).SetOpen(true);
    }

    [NetRpc(RpcTarget.All)]
    private void SetOpen(bool open) => transform.localRotation = Quaternion.Euler(0f, open ? 90f : 0f, 0f);

    public void Use() => Rpc.RequestOpen();
}
```

## When can you call?

The object must be spawned and the client must be in a room. Otherwise the call logs an error and does nothing. Call RPCs from `OnNetworkSpawned` or later, not from `Awake` or `OnEnable` ([lifecycle](lifecycle.md)).

Received RPCs run on the main thread. If the object does not exist on a receiver yet, the call is dropped there. Events that arrive while a network scene is loading are held and replayed in order afterwards ([scenes](scenes.md)).

## Old style calls

The previous API, `CallRpc("Name", target, args)`, still works on `[NetRpc]` methods but uses reflection and string names. It is covered under [low-level API](low-level-api.md#reflection-rpcs), and the analyzer (NP0011) suggests the generated call instead.
