# Async

Every request that takes time returns a `NetOperation` (or `NetOperation<T>` when it has a result). One operation type works with `await`, with coroutines and with callbacks. It needs no `Awaitable`, so it runs the same on Unity 2021.3 and Unity 6.

```csharp
using System.Collections;
using NetPulse;
using UnityEngine;

public sealed class OperationStyles : MonoBehaviour
{
    private async void AsAwait()
    {
        var room = await Pulse.JoinOrCreateRoom("arena");
        Debug.Log($"Joined {room.Name}");
    }

    private IEnumerator AsCoroutine()
    {
        var operation = Pulse.JoinOrCreateRoom("arena");
        yield return operation;
        Debug.Log(operation.IsSuccess ? operation.Result.Name : operation.Error.ToString());
    }

    private void AsCallback()
    {
        Pulse.JoinOrCreateRoom("arena").Completed += operation => Debug.Log(operation.IsSuccess ? "ok" : operation.Error.ToString());
    }
}
```

## The operation object

| Member | Meaning |
|---|---|
| `IsDone` | The operation finished, successfully or not |
| `IsSuccess` | Done, with no error and not canceled |
| `HasError`, `Error` | A failure. `Error` is a `NetError` with `ReturnCode` and `Message`. |
| `IsCanceled` | `Cancel()` was called before it finished |
| `Result` | The value of a `NetOperation<T>`, valid after success |
| `Completed` | An event raised when the operation finishes. If it already finished, a new subscriber is called at once. |
| `Cancel()` | Stops waiting, see below |

`NetOperation` is a `CustomYieldInstruction`, so `yield return operation` waits for it without extra code. The continuation of an `await` and the `Completed` handlers run on the main thread, in the NetPulse update, so you can use Unity objects freely.

## Errors

- `await` throws `NetPulseException` for a failed operation. It carries `ReturnCode` and the message. See [codes and keys](../reference/codes.md) for server codes and [connecting](connecting.md#operation-errors) for the client-side ones (`Timeout`, `Disconnected`, `InvalidState`).
- In a coroutine or callback, check `HasError` and `Error`. Nothing is thrown.
- An exception thrown by a `Completed` handler is logged and does not affect the other handlers.
- Wrap `await` in `try/catch` inside an `async void` method. Otherwise an exception reaches Unity's log without any context.

```csharp
using NetPulse;
using UnityEngine;

public sealed class SafeJoin : MonoBehaviour
{
    private async void Start()
    {
        try
        {
            await Pulse.Connect();
            await Pulse.JoinRoom("arena");
        }
        catch (NetPulseException e)
        {
            Debug.LogWarning($"Failed with {e.ReturnCode}: {e.Message}");
        }
    }
}
```

## Cancelling

`operation.Cancel()` completes the operation at once with `IsCanceled` set. Awaiting a canceled operation throws `OperationCanceledException`. For waits that live only on the client (`WaitForPlayersAsync` and `WaitUntilAsync`), `Cancel()` removes the wait. For requests that were already sent to the server, such as `Connect()`, `JoinRoom()` or the ownership calls, it only stops you from waiting: the request still reaches the server and takes effect there.

```csharp
using System;
using NetPulse;
using UnityEngine;

public sealed class WaitForOpponents : MonoBehaviour
{
    private NetOperation _waiting;

    public async void Begin()
    {
        _waiting = Pulse.WaitForPlayersAsync(2);

        try
        {
            await _waiting;
            Debug.Log("Opponent found");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Stopped waiting");
        }
    }

    public void Stop() => _waiting?.Cancel();
}
```

## Waiting helpers

| Call | Completes when |
|---|---|
| `Pulse.WaitForPlayersAsync(count)` | The room has at least `count` players (inactive players count). It completes at once if that is already true. Fails with `InvalidState` outside a room or if you leave the room while waiting. |
| `Pulse.WaitUntilAsync(Func<bool> condition)` | `condition()` returns true. It is evaluated at once and then once per frame. If the condition throws, the operation fails with `InvalidState` and the exception message. |
| `networkObject.RequestOwnershipAsync(timeoutSeconds)` | A `NetOperation<bool>`, see [ownership](ownership.md) |
| `networkObject.TransferOwnershipAsync(player, timeoutSeconds)` | A `NetOperation<bool>` |

```csharp
using NetPulse;
using UnityEngine;

public sealed class StartWhenReady : MonoBehaviour
{
    private bool _ready;

    public void MarkReady() => _ready = true;

    private async void Start()
    {
        await Pulse.WaitForPlayersAsync(2);
        await Pulse.WaitUntilAsync(() => _ready);
        Debug.Log("Everyone is here and ready");
    }
}
```

## Your own operations

`NetOperation.Succeeded()`, `NetOperation.Failed(code, message)`, `NetOperation<T>.Succeeded(result)` and `NetOperation<T>.Failed(code, message)` create finished operations. They are useful when a method of your own has to return the same kind of object as NetPulse calls, for example in tests or when a precondition fails early.
