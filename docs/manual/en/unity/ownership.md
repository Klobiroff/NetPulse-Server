# Ownership

Every network object has one owner, and only the owner sends the object's state. The server keeps the registry of owners and checks every change, so a client never decides by itself that it owns something. `IsMine` and `Owner` change only when the server's ownership event arrives, including for requests you sent yourself.

## Modes

The mode is the `Ownership Mode` field of the `NetworkObject` (`OwnershipMode` in code). It is sent to the server with the spawn.

| Mode | Who can become the owner |
|---|---|
| `Fixed` | Nobody. The creator keeps the object. `RequestOwnership` and `TransferOwnership` are refused. |
| `Takeover` | Anyone. `RequestOwnership()` takes it at once. The owner or the master can also hand it to a player with `TransferOwnership(player)`. |
| `Request` | `RequestOwnership()` asks the current owner, who can approve. Only the owner or the master can assign with `TransferOwnership(player)`. |

The master can reassign any object that is not `Fixed`.

## Requesting and approving

In `Request` mode the owner receives the request as `OnOwnershipRequest(Player requester)`. The owner answers by calling `ApproveOwnership(requester)`. Denying means doing nothing: the request expires on the server (after 5 seconds by default, a server setting) and the requester is not told. A requester can have one pending request per object at a time.

```csharp
using NetPulse;
using UnityEngine;

public sealed class Crate : NetworkBehaviour
{
    public override void OnOwnershipRequest(Player requester)
    {
        if (requester.GetProperty("team", 0) == Owner.GetProperty("team", 0))
        {
            NetworkObject.ApproveOwnership(requester);
        }
    }

    public override void OnOwnerChanged(Player previous, Player current) => Debug.Log($"Now owned by {current?.NickName}");

    public void Grab() => NetworkObject.RequestOwnership();
}
```

`OnOwnershipRequest` runs only on the owner's client. The same request is also raised as `Pulse.Events.OnOwnershipRequest(networkObject, requester)` and through `IOwnershipCallbacks` ([connecting](connecting.md#states-and-events)). `ApproveOwnership` works only for the current owner; others get a warning.

## Waiting for the result

`RequestOwnership()` and `TransferOwnership()` return immediately and do not tell you the outcome. The async versions do:

```csharp
using NetPulse;
using UnityEngine;

public sealed class Pickup : MonoBehaviour
{
    [SerializeField] private NetworkObject _target;

    public async void TryGrab()
    {
        var granted = await _target.RequestOwnershipAsync(3f);
        Debug.Log(granted ? "Got it" : "Refused or timed out");
    }

    public async void Give(Player player)
    {
        var moved = await _target.TransferOwnershipAsync(player);
        Debug.Log(moved ? "Handed over" : "Not handed over");
    }
}
```

| Call | Result `true` when | Result `false` when |
|---|---|---|
| `RequestOwnershipAsync(timeoutSeconds = 5)` | You are the owner (already, or after the server confirms) | The mode is `Fixed`, you are not in a room, the server refuses, or the time runs out |
| `TransferOwnershipAsync(player, timeoutSeconds = 5)` | The player is the owner (already, or after confirmation) | Same reasons as above |

The operations return a `NetOperation<bool>` and never throw for a refusal. See [async](async.md).

## What happens when ownership changes

When the server announces a change, each `NetworkBehaviour` on the object gets `OnOwnerChanged(previous, current)` and then `OnOwnershipGained()` or `OnOwnershipLost()`. After all behaviours, `NetworkObject.OwnershipChanged` and `Pulse.Events.OnOwnershipChanged` are raised. The new owner starts sending the object's full state. Components adapt: `NetworkRigidbody`, for example, makes the body kinematic on non-owners ([components](components.md)). The exact order is on the [lifecycle](lifecycle.md) page.

## Failures

When the server rejects an ownership operation (`Forbidden`, `ObjectNotFound`), the client logs a warning and raises `Pulse.Events.OnOwnershipFailed(networkObject, returnCode, message)`. The local state stays as it was, and a pending async call completes with `false`. The server also rejects state updates, spawns and destroys from the wrong sender, and the client logs at most one warning per second about those.

A server plugin can veto or change transfers. See [hooks](../plugins/hooks.md). A vetoed request looks like a request that was never approved.

## Scene objects and leaving players

- Scene objects belong to the master (`OwnerActorId` 0 means "the master"). The master registers their modes with the server after it joins and after it becomes master, so a `Takeover` scene object can be grabbed by anyone.
- When a player leaves, the scene objects they had taken return to the master.
- Other objects owned by the player at that moment are destroyed on every client. Objects they had handed to someone else survive. A player who is only inactive keeps their objects.
- Destroying a network object requires being the owner or the master.

In offline mode, `RequestOwnership()` makes you the owner immediately ([offline mode](offline-mode.md)).

The Basic sample has a shared Takeover cube: press `E` to take it, then move it with the arrow keys.
