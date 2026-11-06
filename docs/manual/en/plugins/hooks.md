# Hooks

Hooks are the methods of `RoomPlugin` that the room calls at fixed points. Override the ones you need; the base class continues every call by default.

## How a hook works

Every hook is a `ValueTask` method with the same shape:

```csharp
public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
```

`info` describes the call and carries the decision. `ct` fires when the hook runs out of time. A hook has to reach a decision before it returns:

| Method | Meaning |
|---|---|
| `info.Continue()` | Let the action go ahead. |
| `info.Fail(message)` | Reject the action. The client receives `PluginReportedError` (-20) with your message. |
| `info.Cancel()` | Drop the action quietly. The client receives `Ok`. Offered only on hooks whose `info` derives from `ICancellableCallInfo`. |

Every `info` also has `ActorId` (the actor that caused the call, or 0 when there is none) and `IsProcessed`, which is true once a decision has been made.

You can decide before or after an `await`. The hook can be `async`, and after each `await` the code is back on the room fiber, see [the room runs one thing at a time](overview.md#the-room-runs-one-thing-at-a-time).

## All hooks

| Hook | Runs | `Fail` does | `Cancel` |
|---|---|---|---|
| `OnCreateRoomAsync` | When a room is created, before the first player joins. | Room is closed, creator gets -20. | No |
| `OnBeforeJoinAsync` | A player wants to join and the room is open and not full. | Join is refused with -20. | No |
| `OnJoinAsync` | The player was added to the room, before the join response. | Join is undone, player gets -20. | No |
| `OnLeaveAsync` | A player leaves, disconnects or is kicked, before removal. | Only logged; the leave goes on. | No |
| `OnRaiseEventAsync` | A client raised an event, after the server's own checks, before delivery. | Event is not delivered, sender gets -20. | Not delivered, sender gets `Ok` |
| `OnBeforeSetPropertiesAsync` | A client sets room or player properties, after permission checks. | Nothing applied, sender gets -20. | Nothing applied, sender gets `Ok` |
| `OnSetPropertiesAsync` | Properties were applied, before the change is broadcast. | Only logged. | No |
| `OnBeforeTransferOwnershipAsync` | Ownership of a network object is about to change or be requested. | Change refused with -20. | Nothing changes, sender gets `Ok` |
| `OnBeforeCloseRoomAsync` | The room is empty, or the server is shutting down. | Room stays open, unless the close is forced. | Same as `Fail` |
| `OnCloseRoomAsync` | The room is closing. | No effect. | No |
| `OnUnhandledExceptionAsync` | Plugin code threw. Not a decision hook. | | |

## Hook details

### OnCreateRoomAsync

Runs once, after the factory created the plugin and before the creator joins. The `info` has `Options` (the `RoomOptions` the client sent), `UserId`, `NickName` and `PlayerProperties`. Use it to reject room settings that your game does not allow.

```csharp title="RoomLimitsPlugin.cs"
using NetPulse.Plugins;

namespace HookExamples;

public sealed class RoomLimitsPlugin : RoomPlugin
{
    private const int MaxPlayersLimit = 16;

    public RoomLimitsPlugin(IPluginHost host)
        : base(host)
    {
    }

    public override ValueTask OnCreateRoomAsync(ICreateRoomCallInfo info, CancellationToken ct)
    {
        if (info.Options.MaxPlayers is 0 or > MaxPlayersLimit)
        {
            info.Fail($"MaxPlayers must be between 1 and {MaxPlayersLimit}");
            return default;
        }

        info.Continue();
        return default;
    }
}
```

### OnBeforeJoinAsync and OnJoinAsync

The join sequence is: the server checks that the room is open and not full, then runs `OnBeforeJoinAsync`, adds the player, runs `OnJoinAsync`, sends the join response, announces `PlayerJoined` to the others and replays the event cache to the newcomer.

`OnBeforeJoinAsync` has `UserId`, `NickName`, `IsRejoin` and `PlayerProperties`. For a new player, the `PlayerProperties` table you leave behind becomes the player's initial properties, so you can add or remove keys. `ActorId` is the existing actor id on a rejoin and 0 otherwise. This is the place for checks that need I/O, such as a ban list:

```csharp title="BanCheckPlugin.cs"
using Microsoft.Extensions.Logging;
using NetPulse.Plugins;

namespace HookExamples;

public sealed class BanCheckPlugin : RoomPlugin
{
    private readonly string _banServiceUrl;

    public BanCheckPlugin(IPluginHost host, string banServiceUrl)
        : base(host) => _banServiceUrl = banServiceUrl;

    public override async ValueTask OnBeforeJoinAsync(IBeforeJoinCallInfo info, CancellationToken ct)
    {
        var url = $"{_banServiceUrl}?user={Uri.EscapeDataString(info.UserId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        try
        {
            using var response = await Host.HttpAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                info.Fail("You are banned from this room");
                return;
            }
        }
        catch (HttpRequestException ex)
        {
            Host.Logger.LogWarning(ex, "Ban service is unreachable, letting {User} in", info.UserId);
        }

        info.Continue();
    }
}
```

`OnJoinAsync` has the new `Actor` (an `IActor`) and `IsRejoin`. The player is already in `Host.Actors`, but its client has not received the join response yet. Anything you send to that client from inside the hook arrives before the response. Use `Host.Enqueue` to send after the join has finished, as in [Writing a plugin](writing-a-plugin.md). If you fail `OnJoinAsync`, the server rolls the join back.

### OnLeaveAsync

Runs for every way a player can leave: a `LeaveRoom` request (reason `left`), a disconnect (reason `disconnect`), or a kick (the reason you passed to `Host.KickActor`). `info.Actor` is still in the room. `IsInactive` is true when the player stays as an inactive actor so that it can rejoin, which happens when the room has `PlayerTtlMs` set and the player disconnected or asked to become inactive.

The hook runs once per leave. When an inactive player's time to live expires later, the player is removed without another `OnLeaveAsync`. A failed `OnLeaveAsync` cannot keep the player in the room.

When the whole room closes, players are removed without `OnLeaveAsync`. Use `OnCloseRoomAsync` for work that happens at the end of the room.

### OnRaiseEventAsync

Runs for every event a client raises: your own events (codes 0 to 199) and the object events: instantiate, destroy, RPC, state sync, message and master sync variable. The server has already refused reserved codes and ownership violations by then, see [Security](../server/security.md). The `info.Request` is a `RaiseEventRequest` with `EventCode`, `Data`, `Receivers`, `TargetActors`, `InterestGroup`, `Caching`, `Delivery` and `ViewId`.

The request is mutable. If you continue, the event goes out as it is when the hook returns, so you can rewrite `Data`, change `Receivers` or `Caching`. If `Data` arrives as an `OpaqueValue` (a custom type that the server does not know), you can read it with `NetReader`.

```csharp title="ChatRulesPlugin.cs"
using NetPulse.Plugins;

namespace HookExamples;

public sealed class ChatRulesPlugin : RoomPlugin
{
    private const byte ChatEvent = 20;
    private const int MaxLength = 200;

    private readonly HashSet<int> _muted = new();

    public ChatRulesPlugin(IPluginHost host)
        : base(host)
    {
    }

    public void Mute(int actorId) => _muted.Add(actorId);

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        if (info.Request.EventCode != ChatEvent)
        {
            info.Continue();
            return default;
        }

        if (_muted.Contains(info.ActorId))
        {
            info.Cancel();
            return default;
        }

        if (info.Request.Data is not string text || text.Length > MaxLength)
        {
            info.Fail("Chat messages must be text of up to 200 characters");
            return default;
        }

        info.Continue();
        return default;
    }
}
```

This hook sits on the path of every RPC and state sync of every player, so keep the common case fast: check the event code first and call `Continue()` right away for everything you do not care about.

### OnBeforeSetPropertiesAsync and OnSetPropertiesAsync

`OnBeforeSetPropertiesAsync` runs after the server's permission checks (reserved keys, other players' properties) and before the properties are applied. `TargetActorId` is 0 for room properties and the actor id for player properties. `Properties` is the change (a `null` value deletes a key) and `ExpectedProperties` is the compare-and-set condition the client attached, or `null`. The server evaluates the condition after the hook, so a hook that continues does not skip the check.

Fail to reject with a message, or cancel to ignore the change without telling the client anything is wrong. The [Security](../server/security.md#reserved-properties) page has an example.

`OnSetPropertiesAsync` runs after the properties were applied and before they are broadcast. It has `TargetActorId` and `Properties`. It cannot undo anything: use it to react, for example to recompute a derived value.

### OnBeforeTransferOwnershipAsync

Runs when ownership of a network object is about to change, and when a player asks for an object in `Request` mode. The `info` has `ViewId`, `NewOwnerActorId`, `CurrentOwnerActorId`, `OwnershipMode` and `IsRequest`. `IsRequest` is true for the first step of a `Request`-mode object, when the server is about to forward the request to the owner. The later transfer, when the owner approves, runs the hook again with `IsRequest` set to false.

```csharp title="LockedObjectsPlugin.cs"
using NetPulse.Plugins;

namespace HookExamples;

public sealed class LockedObjectsPlugin : RoomPlugin
{
    private readonly HashSet<int> _locked = new();

    public LockedObjectsPlugin(IPluginHost host)
        : base(host)
    {
    }

    public void Lock(int viewId) => _locked.Add(viewId);

    public void Unlock(int viewId) => _locked.Remove(viewId);

    public override ValueTask OnBeforeTransferOwnershipAsync(IBeforeTransferOwnershipCallInfo info, CancellationToken ct)
    {
        if (_locked.Contains(info.ViewId))
        {
            info.Fail($"Object {info.ViewId} is locked");
            return default;
        }

        info.Continue();
        return default;
    }
}
```

### OnBeforeCloseRoomAsync and OnCloseRoomAsync

`OnBeforeCloseRoomAsync` runs when the last player left (reason `empty`, after `EmptyRoomTtlMs` if the client set one) and when the server shuts down (reason `server shutdown`). Fail or cancel keeps the emptied room alive. The server will ask again the next time the room becomes empty. The shutdown ignores the answer and closes the room anyway. `Host.CloseRoom()` also skips this hook.

`OnCloseRoomAsync` always runs when a room closes. The `Reason` is one of `empty`, `server shutdown`, `closed by plugin`, or a creation failure (`plugin initialization failed`, `create rejected by plugin`, `create failed`).

When `OnCloseRoomAsync` starts, the room has already stopped its timers and cancelled the token of `RunInBackground`, the players are gone, and broadcasting reaches nobody. What is left is the time to save results. Await the work inside the hook; `HookTimeoutMs` applies.

```csharp title="MatchResultPlugin.cs"
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using NetPulse.Plugins;

namespace HookExamples;

public sealed class MatchResultPlugin : RoomPlugin
{
    private readonly string _resultsUrl;

    public MatchResultPlugin(IPluginHost host, string resultsUrl)
        : base(host) => _resultsUrl = resultsUrl;

    public override async ValueTask OnCloseRoomAsync(ICloseRoomCallInfo info, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _resultsUrl)
        {
            Content = JsonContent.Create(new { room = Host.RoomName, reason = info.Reason, closedAtMs = Host.ServerTimeMs })
        };

        try
        {
            using var response = await Host.HttpAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Host.Logger.LogWarning(ex, "Could not save the result of room {Room}", Host.RoomName);
        }

        info.Continue();
    }
}
```

### OnUnhandledExceptionAsync

```csharp
public virtual ValueTask OnUnhandledExceptionAsync(Exception ex, ICallInfo? info)
```

The room calls it when plugin code throws: a hook, a timer callback, or an action passed to `Host.Enqueue`. `info` is the call that failed, or `null` when the exception did not come from a hook. The default implementation logs the exception. Override it to report errors elsewhere. The room keeps running, and the failed call gets `Fail`.

If this method throws itself, the room logs that and carries on.

## Errors and timeouts

- A hook that throws, or whose `ValueTask` faults, goes to `OnUnhandledExceptionAsync`, increments the plugin error counter shown by the `stats` command, and fails the call with the message `Plugin error`.
- A hook that returns without a decision is logged as an error and fails with `Plugin did not respond`.
- Every hook has `NetPulse:Plugins:HookTimeoutMs` (5 seconds by default) to finish, and so does the plugin factory. When time runs out, the call fails with `Plugin hook timed out`, the server logs a warning and cancels `ct`. The room then moves on. A `Continue()` that arrives later is ignored with a warning. Pass `ct` to every `await` that supports it, otherwise the abandoned hook keeps running in the background.
- Calling `Continue`, `Fail` or `Cancel` a second time is ignored with a warning. The first decision wins.
- Exceptions that happen after an `await` are caught the same way as those before it.

Timeouts are logged but do not call `OnUnhandledExceptionAsync`.
