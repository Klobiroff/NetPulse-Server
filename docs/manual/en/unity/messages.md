# Messages

A typed message is the simplest way to tell everyone in a room something that does not belong to a particular object: a chat line, a "round started" notice, a vote. You define a struct or class, send it with `Pulse.Send`, and receive it with `Pulse.On<T>`. There are no event codes and no registration calls, because the source generator writes the serialization code at compile time.

## Define, send, receive

Declare the message as a `partial` type that implements `INetMessage`:

```csharp
using System;
using NetPulse;
using UnityEngine;

public partial struct ChatMessage : INetMessage
{
    public string Text;
    public int Channel;
}

public sealed class Chat : MonoBehaviour
{
    private IDisposable _subscription;

    private void OnEnable() => _subscription = Pulse.On<ChatMessage>((message, sender) => Debug.Log($"{sender?.NickName}: {message.Text}"));

    private void OnDisable() => _subscription?.Dispose();

    public void Say(string text) => Pulse.Send(new ChatMessage { Text = text, Channel = 1 }, RpcTarget.All);
}
```

`Pulse.On<T>` returns an `IDisposable`. Dispose it to unsubscribe. The handler gets the message and the `Player` who sent it. The sender can be `null` if that player has already left. You can subscribe at any time, including before you are in a room.

## Sending

`Pulse.Send` has three shapes. All of them need to be called while you are in a room; otherwise they log a warning and send nothing.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public partial struct ChatMessage : INetMessage
{
    public string Text;
}

public sealed class SendExamples : MonoBehaviour
{
    private void Send(Player somePlayer)
    {
        Pulse.Send(new ChatMessage { Text = "hi" });
        Pulse.Send(new ChatMessage { Text = "all" }, RpcTarget.All);
        Pulse.Send(new ChatMessage { Text = "psst" }, somePlayer);
        Pulse.Send(new ChatMessage { Text = "welcome" }, RpcTarget.OthersBuffered);
        Pulse.Send(new ChatMessage { Text = "fast" }, RpcTarget.Others, new MessageOptions { Delivery = DeliveryMode.Unreliable });
    }
}
```

| Overload | Meaning |
|---|---|
| `Send(message)` | To everyone else (`RpcTarget.Others`) |
| `Send(message, target)` | To a target group |
| `Send(message, target, options)` | Same, with `MessageOptions` |
| `Send(message, player, options)` | To one player |

The targets:

| `RpcTarget` | Delivered to |
|---|---|
| `Others` | Everyone except you |
| `All` | Everyone, you included. Your own handlers run immediately, inside the `Send` call. |
| `MasterClient` | The master. If you are the master, only your own handlers run and nothing is sent. |
| `OthersBuffered` | Everyone else now, and every player who joins later (the server caches the message) |
| `AllBuffered` | Like `OthersBuffered`, and your own handlers run too |
| `Owner` | Not valid for messages (there is no object). It logs a warning. |

Sending to a `Player` who is the local player runs only your own handlers.

## MessageOptions

`MessageOptions` has two settings:

- `Delivery`: `ReliableOrdered` (the default), `ReliableUnordered`, `UnreliableSequenced` or `Unreliable`. Use an unreliable mode for data where a lost packet does not matter because a newer one follows.
- `Caching`: `DoNotCache` (the default) or one of the `CachingOption` values. The buffered targets above set the cache for you. `Caching = RemoveFromRoomCache` removes all cached messages from the room, of every message type, and that message itself is not delivered.

Messages are not filtered by interest groups.

## Serializable types

A message type, and any type nested inside it, is described by its public instance fields and its public properties that have both a getter and a setter and are written as auto-properties. They are written in declaration order. Read-only fields, constants, statics and computed properties are skipped.

Supported field types:

- `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `char`, `string`, `Guid`, and enums
- `Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Color`, `Color32`, `Vector2Int`, `Vector3Int`, `Rect`
- `Player` and `NetworkObject` (sent as the actor id or view id; the receiver gets `null` if the player or object is not known there)
- one-dimensional arrays and `List<T>` of any of these
- other types marked `[NetSerializable]` or implementing `INetMessage`

```csharp
using System.Collections.Generic;
using NetPulse;
using UnityEngine;

[NetSerializable]
public partial struct Stats
{
    public int Hp;
    public bool Alive;
}

public partial class RoundSummary : INetMessage
{
    public Stats Winner;
    public Vector3[] Spawns;
    public List<string> Names;
    public Player Mvp { get; set; }
}
```

`[NetSerializable]` is the same mechanism for types that are not messages. Use it for values you pass as RPC arguments, store in properties or put inside other messages. The rules:

- The type must be `partial`, and so must any type that contains it.
- It cannot be generic.
- A class needs a parameterless constructor. It is written with a leading null flag, so a `null` class instance arrives as `null`. A struct has no such flag.
- `null` and empty arrays or lists look the same on the wire, and a `null` collection arrives as an empty one.

The analyzer reports a violation as a compile error ([analyzers](analyzers.md)). The message id is a hash of the type's full name, so renaming or moving a type changes it, and both sides must run the same version. A receiver that gets an id it does not know logs one warning and ignores the message.

## Handlers

- Several handlers for one type are allowed. They run in the order they were added.
- An exception in a handler is logged, and the remaining handlers still run.
- Messages that arrive while a network scene is loading are held and replayed afterwards ([scenes](scenes.md)).
