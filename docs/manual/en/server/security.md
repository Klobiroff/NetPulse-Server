# Security

The server treats every client as untrusted. This page lists what it checks on its own, and where you have to add checks yourself.

## Connection key, AppId and version

Three values gate who can talk to the server:

- The connection key (`NetPulse:ConnectionKey`) is checked by the transport. A client with another key never gets a connection.
- The AppId (`NetPulse:AppId`) is checked when the client authenticates. If you set it, a client that sends another value gets `InvalidAppId`.
- The protocol version is checked at the same time when `NetPulse:AppVersionCheck` is `true`. A mismatch returns `VersionMismatch`.

All three ship inside your game, so anyone who unpacks the build can read them. They keep unrelated clients and outdated builds out. They do not prove that a client is a legitimate player.

A client that fails the AppId or version check stays connected but unauthenticated, and the server answers every other operation with `NotAuthenticated`.

## Authentication

Without an authentication plugin the server accepts every client that passes the checks above. The `UserId` and `NickName` the client sends become its identity. Two consequences follow:

- A client can claim any `UserId`. If you use `PlayerTtlMs` so that players can rejoin, another client that sends the same `UserId` takes over the inactive player's slot.
- A nickname is only a label. Do not use it for permissions.

To authenticate players, install a plugin that implements `IAuthPlugin` and set `NetPulse:Plugins:Auth` to its name. The server then uses the `UserId` and `NickName` the plugin returns and ignores the client's. The repository ships `AuthSample`, which verifies HMAC-signed tokens or asks your HTTP service. See [Authentication](../plugins/authentication.md).

The server fails closed:

- If `NetPulse:Plugins:Auth` is set and the plugin is missing, throws while loading, or does not provide an `IAuthPlugin`, the server refuses to start.
- If the plugin throws while checking a client, the client gets `AuthenticationFailed`.
- A rejected client, or one that exceeds `AuthTimeoutMs`, gets `AuthenticationFailed` and is disconnected about a quarter of a second later.

!!! warning "Traffic is not encrypted"
    NetPulse does not encrypt UDP traffic. Authentication tokens and game data cross the network in clear text. Use tokens that expire quickly, such as the AuthSample HMAC tokens with a short TTL, and never send a password as the token.

## Flood protection

Each peer may have at most `NetPulse:PeerQueueLimit` messages (256 by default) queued and not yet processed. The count covers the lobby and the room together. When a peer exceeds it, the server drops the message and disconnects the peer with the reason `Overloaded`. The `netpulse.peers_overloaded` metric counts these.

A second bound, `NetPulse:RoomQueueCapacity` (4096), caps the messages waiting in one room or in the lobby. When it is full, the message is dropped and the peer with the most queued messages is disconnected, which may be a different peer than the sender. Server-internal work, such as timers and `Host.Enqueue`, does not count against either limit.

What this does and does not cover:

- It limits work waiting in a queue, so a client that floods a slow room is removed before it can grow the queue without bound.
- It does not limit messages per second or bytes per second. A fast room that keeps up never reaches the limit.
- Keep-alive pings and authentication requests bypass the queues.
- `NetPulse:MaxConnections` caps the number of connected peers, but there is no limit on connection attempts per address. Use the firewall for that.

## Ownership validation

The server keeps a registry of network objects in every room and checks each message that touches one. A rejected message is dropped, the sender gets `Forbidden` (or an `ErrorInfo` event if the request had no request id, at most once a second), the server logs a rate-limited warning, and `netpulse.ownership_violations` counts it. Nobody is disconnected for this.

| Message | Accepted when |
|---|---|
| Object instantiate (event 240) | The view id is at least 1000, belongs to the sender (`viewId / 1000 == actorId`) and is not in use. |
| Object destroy (event 241) | The sender owns the object, or is the master client. Scene objects cannot be destroyed. |
| State sync (event 243) | The sender is the current owner. |
| Master sync variable set (event 245) | The sender is the master client. |
| Transfer ownership | The object's mode allows it: `Fixed` never changes; `Takeover` lets anyone take it; the owner or the master can assign to anyone; `Request` lets only the owner or the master assign. |
| Register scene objects | The sender is the master client. |
| RPC (event 242) and messages (event 244) | Always accepted. Validate them in a plugin if they matter. |

Scene objects (view ids 1 to 999) belong to the master client until the master assigns them to someone. When a player leaves for good, it loses the objects it currently owns; its scene objects return to the master. An inactive player (waiting to rejoin) keeps everything.

A plugin can add its own veto with `OnBeforeTransferOwnershipAsync`, see [Hooks](../plugins/hooks.md).

## Reserved events

A client may raise event codes 0 to 199 (your own events) and the object events 240 to 245: instantiate, destroy, RPC, state sync, message and master sync variable. Any other code is reserved for the server and the request fails with `Forbidden` and the message `Reserved event code`. The server events that clients receive include the room list, join and leave notices, property changes, master and ownership changes, and `ErrorInfo`. The complete list is in [Codes and keys](../reference/codes.md).

Plugins are not bound by this rule: `Host.BroadcastEvent` can send any code.

## Reserved properties

Four room property keys control the room itself, so only the master client may change them:

| Key | Controls |
|---|---|
| `_isOpen` | Whether new players may join. |
| `_isVisible` | Whether the room appears in the lobby list. |
| `_maxPlayers` | The player limit, clamped to 0 to 255. |
| `_scene` | The current network scene. A change removes scene-bound objects. |

A non-master client that includes one of these keys in a room property update gets `Forbidden` and nothing is applied. Only the master client may change another player's properties; everyone may change their own. A plugin can change reserved keys through `Host.SetRoomProperties`, which skips these checks.

Every other room property can be written by any client in the room. Do not keep authoritative values such as scores or inventories there unless you guard them. The easiest guard is a plugin:

```csharp title="ProtectedKeysPlugin.cs"
using NetPulse.Plugins;

namespace ProtectedKeys;

public sealed class ProtectedKeysPlugin : RoomPlugin
{
    private const string ServerPrefix = "server_";

    public ProtectedKeysPlugin(IPluginHost host)
        : base(host)
    {
    }

    public override ValueTask OnBeforeSetPropertiesAsync(IBeforeSetPropertiesCallInfo info, CancellationToken ct)
    {
        foreach (var key in info.Properties.Keys)
        {
            if (key.StartsWith(ServerPrefix, StringComparison.Ordinal))
            {
                info.Fail($"Property '{key}' is read only");
                return default;
            }
        }

        info.Continue();
        return default;
    }
}
```

The plugin writes `server_*` keys itself with `Host.SetRoomProperties`, which does not go through the hook.

## Checklist

1. Set `NetPulse:AppId` and keep `AppVersionCheck` on.
2. Configure an authentication plugin and short-lived tokens.
3. Put authoritative state behind a plugin and validate events in `OnRaiseEventAsync`.
4. Keep the default flood limits unless you measured a reason to change them, and watch `netpulse.peers_overloaded` and `netpulse.ownership_violations`.
5. Open only the UDP port, and restrict it with a firewall where you can.
