# State sync

State sync keeps values on a network object equal on every client. NetPulse offers `SyncVar<T>` for most cases, and `[Synced]` fields plus `OnSerialize` for streaming custom data. Built-in components such as `NetworkTransform` use the same machinery ([components](components.md)).

## Which one to use

| Need | Use |
|---|---|
| A value with a change callback: health, score, a color, a name | `SyncVar<T>` |
| A value that only the master may change | `SyncVar<T>` with `SyncPermission.Master` |
| A value that was set once and is read by newcomers | A [room or player property](rooms-and-properties.md) if it is not tied to an object, otherwise `SyncVar<T>` |
| A one-time event on an object | An [RPC](rpc.md) |
| Custom data that changes constantly, written by hand (aim direction, a compressed path) | `OnSerialize` with `NetStream` |
| A few primitives with no callbacks, in older code | `[Synced]` fields |

New code should reach for `SyncVar<T>` first. It is type-safe, supports many more types than `[Synced]`, and tells you when the value changed.

## SyncVar

```csharp
using NetPulse;
using UnityEngine;

public sealed partial class Hero : NetworkBehaviour
{
    private readonly SyncVar<int> _health = new(100);
    private readonly SyncVar<string> _title = new(string.Empty, SyncPermission.Master);

    public int Health => _health.Value;

    public override void OnNetworkSpawned() => _health.Changed += (previous, current) => Debug.Log($"{name}: {previous} -> {current}");

    public void Damage(int amount) => _health.Value -= amount;

    public void Rename(string title) => _title.Value = title;
}
```

Rules:

- Declare the field `readonly` inside a `partial` class that derives from `NetworkBehaviour`. The source generator registers it. The analyzer warns about a field that is not `readonly` or is outside a `NetworkBehaviour` (NP0008), and rejects an unsupported `T` (NP0007).
- `T` can be any type listed under [serializable types](messages.md#serializable-types): primitives, strings, enums, Unity vectors and colors, `Player`, `NetworkObject`, arrays, lists and `[NetSerializable]` types.
- Read with `.Value`, or rely on the implicit conversion to `T`.
- `Changed(previous, current)` fires on every client when the value changes, for your own writes and for values that arrive from the network. An exception in a handler is logged and does not stop the other handlers.
- Assigning a value equal to the current one does nothing. Equality is `EqualityComparer<T>.Default`, so for arrays and lists it compares references. Changing the contents of a list in place is not noticed; assign a new list to publish the change.

### Permissions

| Permission | Who may write | How it travels |
|---|---|---|
| `Owner` (default) | The owner of the object | As a delta inside the object's regular state update, on a tick boundary |
| `Master` | The master client | As its own message, sent in the next frame after the write |

A write by someone without permission is ignored, and one warning per field is logged. The write is accepted while the object is not yet spawned or you are not in a room, so initial values can be set freely. The server rejects state updates from non-owners and master variables from non-masters as well.

A behaviour can have up to 32 `Owner` variables. Extra ones are not synchronized, and an error is logged. The total limit across both kinds is 256 per behaviour.

### Late joiners

A player who joins later receives the full value of every variable: owners resend their full state when someone enters, and the master sends its variables to the newcomer. For a remote object whose behaviours have state, `OnNetworkSpawned` waits until that first state has been applied, for at most one second, so the first thing the callback sees is real data ([lifecycle](lifecycle.md)).

## Streaming custom data with OnSerialize

Override `OnSerialize(NetStream stream)` to write and read your own bytes. The owner's call has `stream.IsWriting` true, and every other client's call has `stream.IsReading` true. The `Serialize(ref value)` form is symmetric, so one body handles both directions.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class AimSync : NetworkBehaviour
{
    private Vector3 _aim;
    private int _ammo;
    private float _heat;

    public Vector3 Aim => _aim;

    public int Ammo => _ammo;

    public float Heat => _heat;

    protected override DeliveryMode SerializeDelivery => DeliveryMode.UnreliableSequenced;

    public override void OnSerialize(NetStream stream)
    {
        stream.Serialize(ref _aim);
        stream.Serialize(ref _ammo);
        stream.Serialize(ref _heat);
    }
}
```

`NetStream` offers:

- `Serialize(ref x)` for `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `string`, `byte[]`, `Vector2`, `Vector3`, `Quaternion` and `Color`, and `Serialize(INetSerializable)` for your own types that implement `Serialize(NetStream)`.
- `SendNext(object)` and `ReceiveNext<T>()` for the PUN style. The type you read must match the type you wrote exactly, or you get `default`.
- `IsWriting`, `IsReading`, `Remaining` (bytes left to read) and `Timestamp`, the server time at which the owner produced the state.

```csharp
using NetPulse;
using UnityEngine;

public sealed class PunStyleSync : NetworkBehaviour
{
    private int _ammo;
    private Vector3 _lookAt;

    public override void OnSerialize(NetStream stream)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(_ammo);
            stream.SendNext(_lookAt);
            return;
        }

        _ammo = stream.ReceiveNext<int>();
        _lookAt = stream.ReceiveNext<Vector3>();
    }
}
```

How it is sent: on each send tick the owner calls `OnSerialize`, compares the bytes with the previous ones and sends only when they differ. Because it runs on every send tick for every owned object, keep it cheap and avoid allocations.

Override `SerializeDelivery` to pick the delivery mode of a behaviour's state. The default is `ReliableOrdered`. For `UnreliableSequenced` and `Unreliable`, state is also resent once after one second of silence, so a lost packet cannot leave a client stale for long. Behaviours with different delivery modes on one object go out in separate packets.

## Synced fields

`[Synced]` marks a field of a `NetworkBehaviour` for automatic sync, without a callback:

```csharp
using NetPulse;
using UnityEngine;

public sealed class Scoreboard : NetworkBehaviour
{
    [Synced] private int _score;
    [Synced] private Color _tint = Color.white;

    public int Score => _score;

    public Color Tint => _tint;

    public void Add(int points) => _score += points;
}
```

Supported types are `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `string`, `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int`, `Vector3Int` and enums; the analyzer rejects others (NP0010). Fields are sorted by name, at most 32 per behaviour, and the owner polls them on each send tick through reflection and sends the ones that changed. A receiving client writes the field directly, so you get no event and have to poll or check it in `Update`. Prefer `SyncVar<T>` when you need to react to a change.

## Send rate

State goes out on tick boundaries, only from the owner, and only for objects that are spawned. The rate is the lower of `Send Rate` and `Serialization Rate` in the settings, where `Serialization Rate = 0` means the server tick rate. [Ticks and time](ticks-and-time.md) shows the arithmetic. A change you make in `OnNetworkTick` is included in the update sent at the end of that same tick.
