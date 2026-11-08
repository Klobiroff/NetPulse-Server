# Changelog

Changes to the server, the plugin SDK and the shared protocol library (`NetPulse.Common`). The Unity package keeps its own list: see the [Unity package changelog](https://github.com/Klobiroff/NetPulse-Unity/blob/main/Packages/com.netpulse.unity/CHANGELOG.md). A client of version 0.4 needs a 0.4 server.

## 0.4.0 (2026-11-08)

Transport encryption and server hardening. A 0.4 client needs a 0.4 server. With the default `Encryption:Mode` of `Disabled` the wire format of 0.3 is unchanged, and a 0.3 client still connects.

Encryption:

- The whole UDP stream, including LiteNetLib's headers, acknowledgements and disconnect messages, can be encrypted and authenticated. The algorithms are fixed: X25519 with the server's pinned key through the Noise pattern `NK` (`NKpsk0` with a pre-shared key), ChaCha20-Poly1305 records and SHA-256/HKDF. Nothing is negotiated.
- `NetPulse:Encryption`: `Mode` (`Disabled`, `Optional`, `Required`), `PrivateKeyFile` or `NETPULSE_SERVER_PRIVATE_KEY`, `PreviousPrivateKeyFiles` for key rotation, `PreSharedKey` and `PreSharedKeyFile`, `RequireCookie`, `CookieLifetimeMs`, `RekeyAfterPackets`, `RekeyAfterMs`, `ReplayWindow`, `SessionIdleTimeoutMs`, `ReplaceIdleMs`, `HandshakeTimeoutMs` and `PaddingBlock`. The server refuses to start in `Optional` or `Required` mode without a valid key.
- `NetPulse.Server keygen [--out file] [--force]` creates a private key file and prints the public key.
- A cookie exchange runs before any key agreement and every handshake reply is shorter than its request, so the server cannot be used to amplify traffic. A session can be replaced from the same address only after it has been silent for `ReplaceIdleMs`, and the old peer is disconnected first, so a new session never inherits an authenticated peer.
- `NetPulse.Common.Security`: managed, constant-time X25519 and ChaCha20-Poly1305 without allocations on the packet path, the Noise handshake, cookies, the replay window, record protection with rekeying, and the client and server cores. They are checked against the RFC 7748, 8439 and 5869 vectors and against BouncyCastle, the BCL and an independent Noise implementation, in the test projects only.
- Plugin SDK: `AuthRequest.IsEncrypted` and `AuthRequest.ChannelBinding` (the 32-byte handshake hash), `IActor.IsEncrypted`.
- Metrics `netpulse.handshakes_ok`, `handshakes_failed`, `handshakes_rate_limited`, `cookies_sent`, `decrypt_failures`, `replays`, `rekeys` and `plaintext_rejected`.
- `NetPulse.TestClient` can encrypt: `--encrypt --server-public-key KEY [--psk KEY] [--padding N]`.

Limits (`NetPulse:Security`, `0` turns a limit off):

- Per address: `MaxConnectionsPerIp`, `ConnectionAttemptsPerIpPerSecond`, `HandshakesPerIpPerSecond`; server-wide `MaxHandshakesPerSecond`. They also work without encryption where they apply.
- Per peer: `AuthDeadlineMs`, `MaxAuthAttempts`, token buckets `MaxMessagesPerSecond`/`MessageBurst` and `MaxBytesPerSecond`/`ByteBurst`, and `MaxMessageBytes`. A violation disconnects the peer with the reason `RateLimited`, `AuthTimeout` or `TooManyAuthAttempts`.
- Sizes: `MaxUserIdLength`, `MaxNickNameLength`, `MaxAuthTokenLength`, `MaxRoomNameLength`, `MaxPropertiesPerRoom`, `MaxPropertyBytes`, `MaxCachedEventsPerRoom`, `MaxTargetActors`. A request over a limit is refused with `InvalidRequest`, it is not truncated. Control characters are refused in user ids, nicknames and room names, and removed from text before it is logged.
- `RequireRejoinToken`: the server issues a random token on every join and demands it when an inactive player returns, so another client with the same `UserId` cannot take the seat. New protocol field `RejoinToken` (`ParamKey` 56).
- `NetReader` refuses more than 4096 nested containers in one message.
- New metrics `netpulse.peers_rate_limited`, `connections_rejected`, `auth_timeouts` and `limit_violations`.

Fixes:

- A client that joined the lobby could receive a stale room list delta right after the full list. The lobby now flushes pending deltas before it adds a subscriber.

Tests: 292 in total, 112 for Common, 34 for the generators and 146 for the server. The new tests include end-to-end runs over real UDP in `Disabled`, `Optional` and `Required` mode through a recording proxy, with replayed, modified, held and dropped datagrams.

## 0.3.0 (2026-11-06)

Support for the lighter Unity API and the source generator.

Protocol and `NetPulse.Common`:

- New value type code `Extended` (18) with `NetTypeRegistry.RegisterExtended`, `IsExtendedRegistered` and `UnregisterExtended`. An extended value carries a `ushort` id and a length. A server or client that does not know an id keeps it as an `OpaqueValue` and forwards it unchanged.
- `NetWriter` and `NetReader` gained `sbyte`, `ulong` and `char` methods.
- New events `Message` (244) for typed messages and `SyncVarSet` (245) for master-owned sync variables.
- New reserved room property `_scene`, and `RoomPropertyKeys.Scene`.
- `RaiseEventRequest.ObjectFlags` with the `PersistAcrossScenes` flag.
- The join response includes `TickRate`.

Server:

- `SyncVarSet` and changes to `_scene` are accepted only from the master client.
- A scene change removes scene objects and objects that are not persistent, together with their cached events.
- `NetPulse:TickRate` is now also sent to clients, which use it for their network tick.

Plugin SDK:

- `INetworkObject.IsPersistent`.

Tools and tests:

- `NetPulse.Unity.Generators`: the Roslyn source generator and analyzers NP0001 to NP0011 for the Unity package, and `tools/build-generators.ps1` to copy the built dll into the package.
- 159 tests: 41 for Common, 34 for the generators and 84 for the server.

## 0.2.0 (2026-11-06)

The first version in the git history.

- Async rooms: every room runs on its own fiber that awaits each queued item, so one slow room never delays another room, the lobby or the network thread.
- Plugin hooks are asynchronous (`ValueTask` with a `CancellationToken`) and have a timeout (`NetPulse:Plugins:HookTimeoutMs`). The synchronous hooks and `Defer()` were removed. A hook that finishes without a decision counts as `Fail`.
- Host API: async timers backed by `PeriodicTimer`, `Enqueue`, `HttpAsync` and `RunInBackground`.
- Flood protection per peer (`PeerQueueLimit`), with a bound on each room queue (`RoomQueueCapacity`) and the `Overloaded` disconnect reason.
- Server-validated network object ownership with three modes (`Fixed`, `Takeover`, `Request`), the operations `RequestOwnership` and `RegisterSceneObjects`, and the `OwnershipRequested` event. The `OnBeforeTransferOwnershipAsync` hook lets plugins veto transfers.
- Custom authentication through `IAuthPlugin`, with `AuthTimeoutMs` and a server that refuses to start when the configured authentication plugin cannot load. The `AuthSample` plugin has an HMAC mode and an HTTP mode, and the test client can create tokens with `--make-token`.
- Plugin hot reload with isolated load contexts, and the `reload` console command.
- Lobby with room list deltas, random join with property filters, player and empty-room time to live, compare-and-set property updates, interest groups and the event cache.
- Metrics under the meter `NetPulse`, and the `stats`, `plugins`, `reload` and `quit` console commands.
- `NetPulse.TestClient`, a bot client for smoke tests.

## 0.1

The initial protocol, `NetPulse.Common`, the lobby and rooms, and the first plugin hooks. This version predates the git history.
