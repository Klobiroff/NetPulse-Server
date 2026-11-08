# Configuration

All options live under the `NetPulse` section. The server reads `appsettings.json` from its working directory, then environment variables, then the command line. See [Running the server](running.md#changing-settings) for the syntax of each source.

Out of the box the shipped `appsettings.json` sets only a few values; every other option takes the default in the tables below.

## Network and capacity

| Key | Default | Effect |
|---|---|---|
| `NetPulse:Port` | `5055` | UDP port the server binds. `0` picks a random free port, which the startup log prints. |
| `NetPulse:ConnectionKey` | `NetPulse` | LiteNetLib connection key. A client with a different key is rejected before it can send anything. The Unity client uses the same default and has a matching field in its settings. |
| `NetPulse:MaxConnections` | `2000` | Maximum number of connected peers. New connection requests above the limit are rejected. |
| `NetPulse:TickRate` | `30` | Update rate of the transport, in ticks per second (the interval is `1000 / TickRate` ms, at least 1 ms). The value is also sent to every client in the join response, and the Unity client uses it for its network tick. Values below 1 are treated as 1. |
| `NetPulse:DisconnectTimeoutMs` | `10000` | A peer that sends nothing for this long is dropped. |
| `NetPulse:RoomListIntervalMs` | `1000` | How often the lobby sends room list changes to subscribed clients. Values below 50 are treated as 50. |

## Application checks

| Key | Default | Effect |
|---|---|---|
| `NetPulse:AppId` | empty | When set, every client must send exactly this AppId in its authenticate request, otherwise it gets `InvalidAppId` (-31). When empty, any AppId is accepted. |
| `NetPulse:AppVersionCheck` | `true` | When `true`, the protocol version sent by the client must equal the server's (currently `1`), otherwise the client gets `VersionMismatch` (-30). The check compares the protocol version, not the `AppVersion` string of your game. |

The AppId is not a secret, because it ships inside your game. Use it to keep unrelated games and test builds apart, and use [authentication](../plugins/authentication.md) to decide who may play. See [Security](security.md).

## Flood protection and timeouts

| Key | Default | Effect |
|---|---|---|
| `NetPulse:PeerQueueLimit` | `256` | Maximum messages one peer may have queued and not yet processed, counted across the lobby and its room. When a peer goes above it, the message is dropped and the peer is disconnected with the reason `Overloaded`. Values below 1 are treated as 1. |
| `NetPulse:RoomQueueCapacity` | `4096` | Hard bound on peer messages queued in one room, and separately in the lobby. When it is full, the new message is dropped and the peer with the most queued messages in that queue is disconnected. That peer is not always the one that just sent. Work the server queues itself (timers, `Host.Enqueue`, shutdown) is exempt. Values below 1 are treated as 1. |
| `NetPulse:OwnershipRequestTimeoutMs` | `5000` | How long an ownership request for a `Request`-mode object waits for the owner to approve before it expires. Values below 1 are treated as 1. |
| `NetPulse:ShutdownTimeoutMs` | `10000` | Time budget for closing all rooms when the server stops. |

## Encryption

Transport encryption lives under `NetPulse:Encryption`. The default `Disabled` installs nothing and leaves the wire format as it was. See [Security](security.md#transport-encryption) for what the modes mean and [Deployment](deployment.md#rotating-the-server-key) for rotating keys.

| Key | Default | Effect |
|---|---|---|
| `NetPulse:Encryption:Mode` | `Disabled` | `Disabled`, `Optional` (plain and encrypted clients) or `Required` (encrypted clients only). With `Optional` or `Required` the server refuses to start without a valid private key. |
| `NetPulse:Encryption:PrivateKeyFile` | empty | File with the Base64 private key made by `NetPulse.Server keygen`. A relative path is resolved against the folder of the server binary. Use this or the variable `NETPULSE_SERVER_PRIVATE_KEY`, not both. |
| `NetPulse:Encryption:PreviousPrivateKeyFiles` | empty list | Retired private keys that are still accepted, so clients that pin an older public key keep working during a rotation. |
| `NetPulse:Encryption:PreSharedKey` | empty | Optional Base64 key (32 bytes) that every client must also have. |
| `NetPulse:Encryption:PreSharedKeyFile` | empty | Same, read from a file. Set at most one of the two. |
| `NetPulse:Encryption:RequireCookie` | `true` | Make a client echo a cookie before the server does any key agreement. Turn it off only on a private network, it removes the protection against spoofed senders. |
| `NetPulse:Encryption:CookieLifetimeMs` | `30000` | How often the cookie secret changes (the previous one is also accepted). At least 1000. |
| `NetPulse:Encryption:RekeyAfterPackets` | `1000000` | Replace the sending key after this many packets. `0` turns the packet trigger off. |
| `NetPulse:Encryption:RekeyAfterMs` | `600000` | Replace the sending key after this long. `0` turns the time trigger off. |
| `NetPulse:Encryption:ReplayWindow` | `4096` | Size of the replay window, in packets. A multiple of 64 from 64 to 65536. |
| `NetPulse:Encryption:SessionIdleTimeoutMs` | `30000` | A session with no valid packet for this long is removed and its peer disconnected. At least 1000. |
| `NetPulse:Encryption:ReplaceIdleMs` | `3000` | How long a session must be silent before a new handshake from the same address may replace it. |
| `NetPulse:Encryption:HandshakeTimeoutMs` | `5000` | How long a half-finished handshake is kept. At least 500. |
| `NetPulse:Encryption:PaddingBlock` | `0` | Pad every outgoing record to a multiple of this many bytes. `0` is off, otherwise 16 to 512. |

## Limits

The `NetPulse:Security` section holds the abuse limits. `0` turns a limit off. The meaning of each key, its default and what happens on a violation are in the table under [Security](security.md#limits-for-connected-clients).

| Key | Default |
|---|---|
| `MaxConnectionsPerIp` | `32` |
| `ConnectionAttemptsPerIpPerSecond` | `20` |
| `HandshakesPerIpPerSecond` | `10` |
| `MaxHandshakesPerSecond` | `2000` |
| `AuthDeadlineMs` | `10000` |
| `MaxAuthAttempts` | `5` |
| `MaxMessagesPerSecond` / `MessageBurst` | `1000` / `2000` |
| `MaxBytesPerSecond` / `ByteBurst` | `1048576` / `2097152` |
| `MaxMessageBytes` | `262144` |
| `MaxUserIdLength` / `MaxNickNameLength` / `MaxAuthTokenLength` | `128` / `64` / `8192` |
| `MaxRoomNameLength` | `128` |
| `MaxPropertiesPerRoom` / `MaxPropertyBytes` | `256` / `16384` |
| `MaxCachedEventsPerRoom` / `MaxTargetActors` | `4096` / `256` |
| `RequireRejoinToken` | `false` |

A negative value stops the server from starting.

## Console

| Key | Default | Effect |
|---|---|---|
| `NetPulse:EnableConsole` | `true` | Read commands from standard input. Set to `false` when the process has no console, for example under a service manager. |

## Plugins

| Key | Default | Effect |
|---|---|---|
| `NetPulse:Plugins:Directory` | `plugins` | Folder that holds one subfolder per plugin. A relative path is resolved against the folder of the server binary, an absolute path is used as is. The server creates the folder if it is missing. |
| `NetPulse:Plugins:Default` | empty | Plugin used by rooms that do not name one in `RoomOptions.Plugin`. Empty means such rooms run without a plugin. |
| `NetPulse:Plugins:Auth` | empty | Name of the plugin that provides authentication (`IAuthPlugin`). When set, the server refuses to start if that plugin cannot be loaded. Empty means no authentication: any client is accepted. |
| `NetPulse:Plugins:HotReload` | `true` | Watch the plugin folder and reload a plugin when its files change. See [Hot reload](../plugins/hot-reload.md). |
| `NetPulse:Plugins:HookTimeoutMs` | `5000` | Time a plugin hook, or a plugin factory, may take. A call that does not finish in time fails with a logged warning. Values below 1 are treated as 1. |
| `NetPulse:Plugins:AuthTimeoutMs` | `5000` | Time `IAuthPlugin.AuthenticateAsync` may take. On expiry the client gets `AuthenticationFailed` (-32) and is disconnected. Values below 1 are treated as 1. |
| `NetPulse:Plugins:Settings:<Plugin>:<key>` | none | String settings handed to the plugin factory as its `config` dictionary. `<Plugin>` is the `name` from `plugin.json` and is matched case-insensitively. Setting keys keep the spelling you wrote, and the dictionary lookup in the plugin is case-sensitive. |

The shipped file configures the sample plugin like this:

```json
{
  "NetPulse": {
    "Plugins": {
      "Settings": {
        "NetPulse.Plugins.Sample": {
          "ScorePerPoint": "1"
        }
      }
    }
  }
}
```

The sample plugin also reads `JoinCheckDelayMs` (default `10`), a delay in milliseconds used by its join check.

## Logging

Logging uses the standard `Logging` section and writes single-line messages to the console. The shipped file sets the default level to `Information` and the `Microsoft` categories to `Warning`. To see connection and disconnection messages, set `Logging:LogLevel:Default` to `Debug`. See [Operations](operations.md#logs).

## A complete example

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  },
  "NetPulse": {
    "Port": 5055,
    "ConnectionKey": "NetPulse",
    "MaxConnections": 2000,
    "AppId": "my-game",
    "AppVersionCheck": true,
    "TickRate": 30,
    "DisconnectTimeoutMs": 10000,
    "RoomListIntervalMs": 1000,
    "PeerQueueLimit": 256,
    "RoomQueueCapacity": 4096,
    "OwnershipRequestTimeoutMs": 5000,
    "ShutdownTimeoutMs": 10000,
    "Encryption": {
      "Mode": "Required",
      "PrivateKeyFile": "server.key",
      "PaddingBlock": 0
    },
    "Security": {
      "MaxConnectionsPerIp": 32,
      "MaxMessagesPerSecond": 1000,
      "MaxMessageBytes": 262144,
      "RequireRejoinToken": false
    },
    "EnableConsole": false,
    "Plugins": {
      "Directory": "plugins",
      "Default": "MyPlugin",
      "Auth": "AuthSample",
      "HotReload": true,
      "HookTimeoutMs": 5000,
      "AuthTimeoutMs": 5000,
      "Settings": {
        "AuthSample": {
          "Mode": "Hmac",
          "Secret": "change-me-long-random-string"
        },
        "MyPlugin": {
          "Greeting": "Welcome"
        }
      }
    }
  }
}
```

!!! note
    Plugin settings always reach the plugin as strings. Parse numbers and booleans in your factory.
