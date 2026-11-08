# Security

The server treats every client as untrusted. This page lists what it checks on its own, what transport encryption gives you, and where you have to add checks yourself.

## What is protected and what is not

Version 0.4 can encrypt and authenticate the whole UDP stream between a Unity client and the server. It defends against:

- eavesdropping: an observer on the network sees no AppId, token, room name or game data;
- forged, modified and replayed packets, including the LiteNetLib acknowledgements and disconnect messages;
- a man in the middle, because the client pins the server's public key;
- hijacking a session from the same address and spoofed senders used to amplify traffic;
- exhausting the server's CPU, memory and connection slots before a client has proven an address;
- abuse by a client that has connected, through per-peer rate limits and size limits.

It does **not** protect you from the player. The client runs on the attacker's machine:

- The process memory, the session keys and the game logic are all readable and editable there. Encryption stops other people from reading or changing the traffic, it does not stop the player from sending whatever they like through a legitimate, encrypted connection.
- Everything that decides fairness must be checked by the server or a plugin. Treat every value from a client as untrusted, encrypted or not.
- Secrets that ship in the build (`ConnectionKey`, `AppId`, the pre-shared key) can be extracted. Obfuscation does not change that and NetPulse does not claim it as protection. The server public key is not a secret, but it must be the real one: it is what stops a man in the middle.
- The transport does not authenticate the player. Any client that knows the server public key can complete the handshake. Use an authentication plugin to know who the player is.
- Not covered in 0.4: anti-cheat, build attestation, a connection that survives a change of the client's address (NAT rebinding drops the connection, as before), mutual authentication with a client key, and post-quantum key exchange.

## Connection key, AppId and version

Three values gate who can talk to the server:

- The connection key (`NetPulse:ConnectionKey`) is checked by the transport. A client with another key never gets a connection.
- The AppId (`NetPulse:AppId`) is checked when the client authenticates. If you set it, a client that sends another value gets `InvalidAppId`.
- The protocol version is checked at the same time when `NetPulse:AppVersionCheck` is `true`. A mismatch returns `VersionMismatch`.

All three ship inside your game, so anyone who unpacks the build can read them. They keep unrelated clients and outdated builds out. They do not prove that a client is a legitimate player. With encryption on, they no longer travel in clear text.

A client that fails the AppId or version check stays connected but unauthenticated, and the server answers every other operation with `NotAuthenticated`.

## Transport encryption

`NetPulse:Encryption:Mode` selects what the server accepts:

| Mode | Behaviour |
|---|---|
| `Disabled` (default) | No encryption layer is installed. The wire format and the per-packet overhead are exactly as in version 0.3. |
| `Optional` | Encrypted and plain clients both connect. A plain packet from an address that already has an encrypted session is dropped, so a plain packet cannot be injected into an encrypted connection. |
| `Required` | Only encrypted clients connect. Every plain datagram is dropped without a reply. |

`Optional` exists for a migration. It cannot protect a client that connects in plain text, and it lets an attacker force a downgrade of a client that is willing to use plain text. The Unity client has no "prefer encryption" mode for that reason: it is either `Off` or `Required`, and a `Required` client never sends or accepts a plain packet. Use `Required` in production.

With `Optional` or `Required` the server refuses to start unless it has a valid private key.

### Algorithms

There is one fixed set of algorithms on every platform and nothing is negotiated, so a downgrade attack has nothing to downgrade:

- key agreement X25519, authenticated with the server's static key through the Noise pattern `NK` (`Noise_NK_25519_ChaChaPoly_SHA256`, or `Noise_NKpsk0_25519_ChaChaPoly_SHA256` when a pre-shared key is set). It gives server authentication and forward secrecy;
- records protected with ChaCha20-Poly1305 (RFC 8439), one key per direction;
- SHA-256 and HKDF for key derivation.

X25519 and ChaCha20-Poly1305 are implemented in managed code in `NetPulse.Common`, because the .NET versions of these ciphers are not available in Unity's Mono and IL2CPP. They run in constant time, without secret-dependent branches or table lookups, and without allocating memory per packet. They are checked against the RFC 7748 and RFC 8439 test vectors and against independent implementations. SHA-256, HMAC and the random number generator come from the platform.

### Keys

Create the server key once:

```bash
NetPulse.Server keygen --out server.key
```

The command writes the private key to the file (it refuses to overwrite an existing file unless you add `--force`) and prints the **public** key as Base64. Put the public key in the Unity settings ([Connecting](../unity/connecting.md#encryption)). Point the server at the private key:

```json
{
  "NetPulse": {
    "Encryption": {
      "Mode": "Required",
      "PrivateKeyFile": "server.key"
    }
  }
}
```

Or give the key in the environment variable `NETPULSE_SERVER_PRIVATE_KEY`. Configure only one of the two. The private key is never part of `appsettings.json`, is never logged, and is not echoed in error messages. Keep the file out of version control and readable only by the account that runs the server. A relative path is resolved against the folder of the server binary.

The server fails closed. It refuses to start when the mode is `Optional` or `Required` and the key is missing, malformed, or configured twice, and when a numeric option is out of range. Rotating the key is described in [Deployment](deployment.md#rotating-the-server-key).

### Pre-shared key

`NetPulse:Encryption:PreSharedKey` (Base64, 32 bytes) or `PreSharedKeyFile` adds a second secret to the handshake. A client without the same value cannot finish it. The value must also be in the client build, so anyone who extracts it can use it: it only filters out clients that do not carry your build. A server with a pre-shared key accepts only clients that use one.

### How a connection starts

1. The client sends an `Init` record with the key id, a Noise message and padding, in place of the first LiteNetLib connect request.
2. The server answers with a short `Retry` that carries a cookie, the HMAC of the sender's address and port. It does no key agreement yet and keeps no state. The reply is always shorter than the request, so the server cannot be used to amplify traffic towards a spoofed address.
3. The client repeats `Init` with the cookie. Only now does the server do the key agreement, and it replies with the second Noise message. A repeated `Init` with the same ephemeral key gets the cached reply without new work.
4. The client sends its LiteNetLib connect request inside the first encrypted record. The server activates the session when it receives the first valid record.

Failures are silent. A wrong server key, wrong pre-shared key, damaged message or unknown key id gets no reply and increments a metric, so the server gives an attacker no oracle. The client sees `SecureHandshakeTimeout`. The cookie secret rotates every `CookieLifetimeMs`, and the current and previous secrets are accepted.

### Records, replay and rekeying

After the handshake every datagram is a record of 26 bytes of overhead: type, key epoch, an 8-byte counter, the encrypted payload and a 16-byte tag. The whole LiteNetLib packet is inside, including its headers, acknowledgements and disconnect messages. The counter only grows and a sliding window (`ReplayWindow`, 4096 by default) rejects duplicates and very old packets. The window moves only after the tag has been verified, so a forged packet cannot move it. A modified byte anywhere in a record fails verification. When the counter approaches its limit, the session is closed.

Each direction replaces its key independently, after `RekeyAfterPackets` packets (1,000,000) or `RekeyAfterMs` (10 minutes), by hashing the old key. Packets that were lost or arrive late still decrypt, and the old key is erased when the replay window has moved past it.

`PaddingBlock` (0 by default, which is off) pads every record to a multiple of the block size, which hides the length of messages from an observer at the cost of bandwidth. It reduces the usable packet size by up to the block size. Use the same value on the client and the server.

### Sessions and address hijacking

Sessions are stored per address and port. A new handshake from an address that has a working session is ignored unless that session has been silent for `ReplaceIdleMs` (3 seconds). Replacing a session always disconnects the LiteNetLib peer bound to the old session first, so a new session never inherits an authenticated peer: whoever takes over an address has to authenticate again. A session is removed when its peer disconnects (after a second, so the last packets can leave) or after `SessionIdleTimeoutMs` without traffic.

### What the plugins see

`AuthRequest.IsEncrypted` and `AuthRequest.ChannelBinding` are passed to the authentication plugin. The channel binding is the Noise handshake hash, 32 bytes that are unique to this connection. A token that contains it is valid only on that connection and cannot be replayed on another. `IActor.IsEncrypted` tells room plugins whether a player's connection is encrypted.

### Metrics

| Metric | Meaning |
|---|---|
| `netpulse.handshakes_ok` | Sessions that were activated |
| `netpulse.handshakes_failed` | Handshakes that failed verification or used an unknown key |
| `netpulse.handshakes_rate_limited` | Handshakes dropped by the per-address limit, the server budget or a busy session |
| `netpulse.cookies_sent` | `Retry` replies sent |
| `netpulse.decrypt_failures` | Records that failed verification |
| `netpulse.replays` | Valid records that were seen before |
| `netpulse.rekeys` | Key replacements, both directions |
| `netpulse.plaintext_rejected` | Plain datagrams dropped in `Required` mode, or from an encrypted address |

### Known limits

- A datagram that the layer drops does not go through LiteNetLib's normal path, and LiteNetLib 1.3.5 does not return its buffer to its pool in that case. A flood of garbage costs allocations on the receive thread. Stop a volumetric flood in the firewall or upstream.
- An outgoing packet that the layer cannot encrypt, for example for an address whose session has already gone, is dropped by sending an empty datagram.
- One key agreement costs about 1 ms of CPU in managed code (three X25519 operations of about 0.3 ms each), so one core completes about 1000 handshakes per second. `MaxHandshakesPerSecond` bounds the total; its default of 2000 is a ceiling for the whole machine, not a promise. Encrypting and decrypting a 1000-byte record costs about 12 microseconds together.

## Authentication

Without an authentication plugin the server accepts every client that passes the checks above. The `UserId` and `NickName` the client sends become its identity. Two consequences follow:

- A client can claim any `UserId`. If you use `PlayerTtlMs` so that players can rejoin, another client that sends the same `UserId` takes over the inactive player's slot, unless you turn on `NetPulse:Security:RequireRejoinToken` (see below).
- A nickname is only a label. Do not use it for permissions.

To authenticate players, install a plugin that implements `IAuthPlugin` and set `NetPulse:Plugins:Auth` to its name. The server then uses the `UserId` and `NickName` the plugin returns and ignores the client's. The repository ships `AuthSample`, which verifies HMAC-signed tokens or asks your HTTP service. See [Authentication](../plugins/authentication.md).

The server fails closed:

- If `NetPulse:Plugins:Auth` is set and the plugin is missing, throws while loading, or does not provide an `IAuthPlugin`, the server refuses to start.
- If the plugin throws while checking a client, the client gets `AuthenticationFailed`.
- A rejected client, or one that exceeds `AuthTimeoutMs`, gets `AuthenticationFailed` and is disconnected about a quarter of a second later.

Authentication tokens are never written to the log. With encryption off they still cross the network in clear text, so use short-lived tokens, such as the AuthSample HMAC tokens with a short TTL, and never send a password as the token.

## Limits for connected clients

`NetPulse:Security` holds the limits. A value of `0` turns a limit off. The defaults are chosen so that normal play never hits them. Violating a limit counts in a metric, and the request is refused or the peer is disconnected as shown.

| Key | Default | When it is exceeded |
|---|---|---|
| `MaxConnectionsPerIp` | `32` | A further connection from the address is rejected. Players behind one NAT share the limit. |
| `ConnectionAttemptsPerIpPerSecond` | `20` | Connection requests above the rate (a burst of twice that is allowed) are rejected. |
| `HandshakesPerIpPerSecond` | `10` | Encryption handshakes above the rate are dropped after the cookie check. Encryption only. |
| `MaxHandshakesPerSecond` | `2000` | Server-wide budget for key agreement. Encryption only. |
| `AuthDeadlineMs` | `10000` | A peer that has not authenticated in time is disconnected with `AuthTimeout`. |
| `MaxAuthAttempts` | `5` | More than this many authenticate requests without success disconnect the peer with `TooManyAuthAttempts`. |
| `MaxMessagesPerSecond` / `MessageBurst` | `1000` / `2000` | Token bucket per peer. The peer is disconnected with `RateLimited`. |
| `MaxBytesPerSecond` / `ByteBurst` | `1048576` / `2097152` | Token bucket per peer, counted in message bytes. Disconnected with `RateLimited`. |
| `MaxMessageBytes` | `262144` | A larger message disconnects the peer with `RateLimited`. |
| `MaxUserIdLength` | `128` | `InvalidRequest`. |
| `MaxNickNameLength` | `64` | `InvalidRequest`. |
| `MaxAuthTokenLength` | `8192` | `InvalidRequest`. |
| `MaxRoomNameLength` | `128` | `InvalidRequest`. |
| `MaxPropertiesPerRoom` | `256` | `InvalidRequest` when a property update or the creation options would exceed it. |
| `MaxPropertyBytes` | `16384` | `InvalidRequest` when one serialized property value is larger. |
| `MaxCachedEventsPerRoom` | `4096` | `InvalidRequest` for an event that would be cached beyond the limit. |
| `MaxTargetActors` | `256` | `InvalidRequest` for an event addressed to more actors. |
| `RequireRejoinToken` | `false` | See below. |

The server refuses a request that exceeds a limit instead of cutting it down. `UserId`, `NickName` and room names must not contain control characters. Text from a client is cleaned of control characters before it is written to the log, so a client cannot forge log lines.

The reader that parses incoming messages checks every declared length against the bytes that are really there before it allocates, and it limits the number of nested containers in one message to 4096, so a small packet cannot make the server build a huge object graph.

### Rejoin tokens

With `PlayerTtlMs` a player who loses the connection keeps the seat for a while and can return with the same `UserId`. Without an authentication plugin that `UserId` is only what the client claims. With `RequireRejoinToken` set to `true`, the server gives each player a random token when they join a room, replaces it on every join, and the Unity client presents it when it returns to the same room. A client that has no valid token for the seat gets `Forbidden`. The token lives in the client's memory, so a player who restarts the game does not return to the seat.

## Flood protection

Each peer may have at most `NetPulse:PeerQueueLimit` messages (256 by default) queued and not yet processed. The count covers the lobby and the room together. When a peer exceeds it, the server drops the message and disconnects the peer with the reason `Overloaded`. The `netpulse.peers_overloaded` metric counts these.

A second bound, `NetPulse:RoomQueueCapacity` (4096), caps the messages waiting in one room or in the lobby. When it is full, the message is dropped and the peer with the most queued messages is disconnected, which may be a different peer than the sender. Server-internal work, such as timers and `Host.Enqueue`, does not count against either limit.

What this does and does not cover:

- It limits work waiting in a queue, so a client that floods a slow room is removed before it can grow the queue without bound.
- The rate and size limits above cover messages and bytes per second.
- Keep-alive pings and authentication requests bypass the queues, and the rate limits still count them.
- `NetPulse:MaxConnections` caps the number of connected peers, and the per-address limits above cap what one address can take. A flood of packets that never connect has to be stopped by the firewall.

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

1. Generate a server key, set `NetPulse:Encryption:Mode` to `Required`, and put the public key into the Unity settings.
2. Set `NetPulse:AppId` and keep `AppVersionCheck` on.
3. Configure an authentication plugin and short-lived tokens. Bind them to `ChannelBinding` if you can.
4. Put authoritative state behind a plugin and validate events in `OnRaiseEventAsync`.
5. Keep the default limits unless you measured a reason to change them, and watch `netpulse.peers_overloaded`, `netpulse.peers_rate_limited`, `netpulse.ownership_violations` and `netpulse.decrypt_failures`.
6. Open only the UDP port, and restrict it with a firewall where you can.
