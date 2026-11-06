# Analyzers

The package ships a Roslyn source generator and analyzers in one DLL, `Runtime/Analyzers/NetPulse.Generators.dll`. Unity applies it to `NetPulse.Runtime` and to every assembly that references it. The generator writes the serialization and dispatch code for `[NetRpc]`, `SyncVar<T>`, `[NetSerializable]` and `INetMessage`. The analyzers check your code against the rules that make that possible and report problems as compiler diagnostics with the ids `NP0001` to `NP0011`. They show up in the Unity console and in any IDE that loads Roslyn analyzers.

| Id | Severity | Rule |
|---|---|---|
| NP0001 | Error | A class with `[NetRpc]`, `SyncVar<T>` or `[NetSerializable]` is not `partial` |
| NP0002 | Error | `[NetRpc]` outside a `NetworkBehaviour` |
| NP0003 | Error | Unsupported RPC parameter type |
| NP0004 | Error | An RPC returns a value or has `ref`, `out` or `in` parameters |
| NP0005 | Error | RPC overload |
| NP0006 | Error | Unsupported member in a `[NetSerializable]` or `INetMessage` type |
| NP0007 | Error | `SyncVar<T>` with an unsupported `T` |
| NP0008 | Warning | A `SyncVar<T>` field is not `readonly` or is outside a `NetworkBehaviour` |
| NP0009 | Error | Id collision (RPCs in a type, messages and serializable types in an assembly) |
| NP0010 | Error | `[Synced]` on an unsupported field type |
| NP0011 | Warning | `CallRpc("Name", ...)` while a `[NetRpc]` method with that name exists |

The examples below show the smallest code that triggers each rule, followed by a fix. Types the generator handles are `partial`, and `sealed partial` is fine.

## NP0001: type must be partial

The generator adds members to your class from a second file, which only works if the class is declared `partial`. The same applies to a type that contains such a class.

```csharp title="NP0001"
using NetPulse;

public sealed class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }
}
```

Add `partial`:

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }
}
```

## NP0002: [NetRpc] outside a NetworkBehaviour

RPCs belong to network objects, so the class has to derive from `NetworkBehaviour`.

```csharp title="NP0002"
using NetPulse;
using UnityEngine;

public sealed partial class Helper : MonoBehaviour
{
    [NetRpc]
    private void Ping()
    {
    }
}
```

```csharp
using NetPulse;

public sealed partial class Helper : NetworkBehaviour
{
    [NetRpc]
    private void Ping()
    {
    }
}
```

## NP0003: unsupported RPC parameter type

Parameters use the types listed under [serializable types](messages.md#serializable-types). A `GameObject` is not one of them.

```csharp title="NP0003"
using NetPulse;
using UnityEngine;

public sealed partial class Spawner : NetworkBehaviour
{
    [NetRpc]
    private void Spawn(GameObject prefab)
    {
    }
}
```

Send a `NetworkObject`, or pass an id and look the object up on the receiving side.

```csharp
using NetPulse;

public sealed partial class Spawner : NetworkBehaviour
{
    [NetRpc]
    private void Spawn(NetworkObject target)
    {
    }
}
```

`NetRpcInfo` is allowed only as the last parameter. Putting it elsewhere also reports NP0003.

## NP0004: invalid RPC signature

An RPC is a one-way call. It must return `void` and take its parameters by value.

```csharp title="NP0004"
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private int Hit(int damage) => damage;
}
```

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }
}
```

If the caller needs an answer, make the answer another RPC that goes back to the sender (`Rpc.To(info.Sender)`).

## NP0005: RPC overload

The id of an RPC is a hash of its method name, so two methods with one name cannot be told apart.

```csharp title="NP0005"
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }

    [NetRpc]
    private void Hit(float damage)
    {
    }
}
```

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }

    [NetRpc]
    private void HitFloat(float damage)
    {
    }
}
```

## NP0006: unsupported member in a serializable type

Public fields and auto-properties of a `[NetSerializable]` or `INetMessage` type must use supported types. Generic types are not supported, and a class needs a parameterless constructor.

```csharp title="NP0006"
using NetPulse;
using UnityEngine;

[NetSerializable]
public partial struct Loadout
{
    public GameObject Prefab;
    public int Slot;
}
```

```csharp
using NetPulse;

[NetSerializable]
public partial struct Loadout
{
    public NetworkObject Prefab;
    public int Slot;
}
```

## NP0007: unsupported SyncVar type

`SyncVar<T>` accepts the same types as messages. Mark a custom type `[NetSerializable]` to use it.

```csharp title="NP0007"
using NetPulse;
using UnityEngine;

public sealed partial class Turret : NetworkBehaviour
{
    private readonly SyncVar<GameObject> _target = new();
}
```

```csharp
using NetPulse;

public sealed partial class Turret : NetworkBehaviour
{
    private readonly SyncVar<NetworkObject> _target = new();
}
```

## NP0008: SyncVar field shape

A `SyncVar<T>` field should be `readonly`, because replacing the field would detach it from the network. A field outside a `NetworkBehaviour` is never synchronized. This is a warning.

```csharp title="NP0008"
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    private SyncVar<int> _health = new(100);
}
```

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    private readonly SyncVar<int> _health = new(100);
}
```

## NP0009: id collision

Ids are 16-bit hashes of names, so two different names can share an id. The analyzer reports it when two RPCs in one class collide, and when two message or `[NetSerializable]` types in one assembly collide. Rename one of them.

```csharp title="NP0009"
using NetPulse;

public sealed partial class Lamp : NetworkBehaviour
{
    [NetRpc]
    private void SendTarget(int id)
    {
    }

    [NetRpc]
    private void ResetLight(int id)
    {
    }
}
```

```csharp
using NetPulse;

public sealed partial class Lamp : NetworkBehaviour
{
    [NetRpc]
    private void SendTarget(int id)
    {
    }

    [NetRpc]
    private void ResetLamp(int id)
    {
    }
}
```

The example is real: both names hash to the same id. Collisions are rare, and when one occurs the diagnostic names both members and the shared id.

## NP0010: unsupported [Synced] field

`[Synced]` fields support only primitives, `string`, a few Unity structs and enums (see [state sync](state-sync.md#synced-fields)). Use `SyncVar<T>` for other types.

```csharp title="NP0010"
using System.Collections.Generic;
using NetPulse;

public sealed class Backpack : NetworkBehaviour
{
    [Synced] private List<int> _slots = new();
}
```

```csharp
using System;
using NetPulse;

public sealed partial class Backpack : NetworkBehaviour
{
    private readonly SyncVar<int[]> _slots = new(Array.Empty<int>());
}
```

## NP0011: use the generated Rpc API

`CallRpc("Name", ...)` with a literal name still works but uses reflection and is not type-checked. When a valid `[NetRpc]` method with that name exists, the analyzer suggests `Rpc.Name(...)`. This is a warning.

```csharp title="NP0011"
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void TakeDamage(int amount)
    {
    }

    public void Damage(int amount) => CallRpc("TakeDamage", RpcTarget.All, amount);
}
```

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void TakeDamage(int amount)
    {
    }

    public void Damage(int amount) => Rpc.TakeDamage(amount);
}
```

The old call stays valid when you build the name at runtime, which the analyzer cannot see ([low-level API](low-level-api.md)).
