# Changelog

Changes to the server, the plugin SDK and the shared protocol library (`NetPulse.Common`). The Unity package keeps its own list: see the [Unity package changelog](https://github.com/Klobiroff/NetPulse-Unity/blob/main/Packages/com.netpulse.unity/CHANGELOG.md). A client of version 0.3 needs a 0.3 server.

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
