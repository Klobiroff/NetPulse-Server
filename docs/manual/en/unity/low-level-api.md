# Low-level API

Everyday code does not need this page. The older, string-and-byte-code API is still supported and unchanged, and you meet it when you maintain 0.2 code, build tools, or need something the typed API does not offer. It covers reflection RPCs, raw events with byte codes, hand-registered serializers, interest groups and the transport.

## Reflection RPCs

`CallRpc(methodName, target, arguments)` calls a `[NetRpc]` method by its name. Use it when the name is only known at runtime, as in `health.Send("Heal", 5)` below:

```csharp
using NetPulse;
using UnityEngine;

public sealed partial class Health : NetworkBehaviour
{
    [Synced] private int _hp = 100;

    public int Hp => _hp;

    public void Send(string rpcName, int amount) => CallRpc(rpcName, RpcTarget.All, amount);

    [NetRpc]
    private void TakeDamage(int amount, NetRpcInfo info)
    {
        _hp -= amount;
        Debug.Log($"{info.Sender?.NickName} dealt {amount}");
    }

    [NetRpc]
    private void Heal(int amount) => _hp += amount;
}
```

How it differs from the generated `Rpc.X()`:

- The method is found with reflection, and the arguments travel as dynamically typed values. They can be anything `PropertyTable` values can be ([rooms and properties](rooms-and-properties.md#the-raw-propertytable)) plus registered types. The receiver converts them to the parameter types.
- The target is the argument you pass. The target in the attribute is not used. `Delivery` and `OwnerOnly` from the attribute are honored.
- A wrong argument count logs an error and sends nothing. An unknown name logs an error that points to the likely cause (a typo, a missing attribute, or IL2CPP stripping, see [IL2CPP](il2cpp.md)).
- When the name is a literal or a `nameof` expression and a valid `[NetRpc]` method with that name exists, the analyzer reports NP0011 and suggests the generated call, for example `Rpc.TakeDamage(amount)`.

Reflection RPCs and generated RPCs both travel as event 242, with a different kind byte and argument layout ([wire protocol](../reference/protocol.md)).

## Raw events

`Pulse.RaiseEvent(code, data, options)` sends an event with a byte code from 0 to 199. A larger code throws `ArgumentOutOfRangeException`. Codes 230 to 250 belong to the system: 230 to 237 are server notifications, 240 to 245 carry spawns, destroys, RPCs, state, typed messages and master variables, and 250 reports errors.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class Flares : NetPulseCallbacks
{
    private const byte FlareEvent = 10;

    public void Launch(Vector3 position)
    {
        var options = new RaiseEventOptions
        {
            Receivers = ReceiverGroup.All,
            Delivery = DeliveryMode.ReliableUnordered
        };
        Pulse.RaiseEvent(FlareEvent, position, options);
    }

    public override void OnEvent(byte eventCode, int senderActorId, object data)
    {
        if (eventCode == FlareEvent && data is Vector3 position)
        {
            Debug.Log($"Flare from actor {senderActorId} at {position}");
        }
    }
}
```

`RaiseEventOptions`:

| Property | Default | Meaning |
|---|---|---|
| `Receivers` | `Others` | `Others`, `All` (the sender too), `MasterClient`, or `Targets` (the actors in `TargetActors`) |
| `TargetActors` | none | Actor ids for `Targets` |
| `InterestGroup` | `0` | Limit to a group, see below |
| `Caching` | `DoNotCache` | Cache behavior, see below |
| `Delivery` | `ReliableOrdered` | Transport mode |

The event arrives as `Pulse.Events.OnEvent(code, senderActorId, data)` or through `IOnEventCallback`. Only your own codes 0 to 199 are reported there. Outside a room, `RaiseEvent` does nothing.

Caching options:

| `CachingOption` | Effect |
|---|---|
| `DoNotCache` | Deliver only |
| `AddToRoomCache` | Deliver and store. The server replays stored events to players who join later. They are removed when their sender leaves. |
| `AddToRoomCacheGlobal` | Like `AddToRoomCache`, but the event stays after the sender leaves |
| `RemoveFromRoomCache` | Remove every stored event with this code. Nothing is delivered. |
| `RemoveFromCacheForActorsLeft` | Remove stored events whose sender is no longer in the room. Nothing is delivered. |

## Custom types and NetTypeRegistry

`data` and RPC arguments can be `null`, `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `string`, `Guid`, `byte[]`, `int[]`, `float[]`, `string[]`, `object[]`, a `PropertyTable` or an `IDictionary<string, object>` with string keys. The Unity types `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int` and `Vector3Int` are registered at start-up under type codes 100 to 105. Other types must be registered, or the send throws a serialization exception.

For new code, mark the type `[NetSerializable]` and the generator registers it for you ([messages](messages.md#serializable-types)). To register a type by hand, pick an unused code from 100 to 199 and use the same code on every client:

```csharp
using NetPulse.Common;
using UnityEngine;

public struct Checkpoint
{
    public int Index;
    public float Time;
}

public static class CheckpointSerializer
{
    public const byte Code = 120;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register() => NetTypeRegistry.Register<Checkpoint>(
        Code,
        static (writer, value) =>
        {
            writer.WriteInt(value.Index);
            writer.WriteFloat(value.Time);
        },
        static reader => new Checkpoint { Index = reader.ReadInt(), Time = reader.ReadFloat() });
}
```

`NetTypeRegistry` also has `RegisterExtended<T>(ushort id, ...)` for the 16-bit ids that generated code uses, `IsRegistered(code)` and `Unregister(code)`. Registering a code twice for different types throws. `NetWriter` and `NetReader` offer `WriteInt`, `WriteFloat`, `WriteString` and the matching `Read` methods for all primitives, and the extension methods of NetPulse add the Unity types.

## State by hand

`[Synced]` fields, `OnSerialize(NetStream)` and `SerializeDelivery` are part of this older layer and are fully described in [state sync](state-sync.md). String property keys (`room.SetProperty("map", "forest")`, `SetProperties(delta, expected)`) are covered in [rooms and properties](rooms-and-properties.md#the-raw-propertytable).

## Interest groups

An interest group limits who receives an event. Group `0` means everyone. Any other group number reaches only the players who subscribed to it:

```csharp
using NetPulse;
using UnityEngine;

public sealed class Zone : MonoBehaviour
{
    [SerializeField] private byte _group = 5;

    public void Enter()
    {
        var add = new byte[] { _group };
        Pulse.ChangeInterestGroups(add, null);
    }

    public void Exit()
    {
        var remove = new byte[] { _group };
        Pulse.ChangeInterestGroups(null, remove);
    }
}
```

- Set the group on an event with `RaiseEventOptions.InterestGroup`, on a network object with its `Interest Group` field, and on a spawn with the `interestGroup` argument of `Pulse.Instantiate`.
- Group filtering applies to `Others` and `All`. Events for the master client or for specific actors ignore it.
- Subscribing needs a room, and does nothing in offline mode. Subscriptions are dropped when a player becomes inactive.
- Cached events are replayed once, when a player joins, and only those whose group the player has at that moment. Subscribing later does not retrieve what was cached before.

## Transport and protocol

`Pulse.UseTransport(INetTransport)` replaces the default `LiteNetLibTransport`. The interface has `Connect`, `Send`, `Poll` and `Disconnect` plus the `Connected`, `Disconnected` and `DataReceived` events and the properties `IsConnected` and `RoundTripTimeMs`. It must be set while disconnected. The wire format is described in [wire protocol](../reference/protocol.md).
