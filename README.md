# NetPulse Server

Self-hosted .NET 10 networking server for Unity (Photon-style: lobby, rooms, properties, event cache, server-side plugins). Transport is LiteNetLib 1.3.5 over UDP, port 5055 by default. The Unity client package lives in `Packages/com.netpulse.unity`; the protocol code in `src/NetPulse.Common` is shared as source (see `tools/sync-common.ps1`).

## Documentation

The user manual is in `docs/manual` in English and Russian. It is built with MkDocs and published to GitHub Pages on every push to `main`: https://klobiroff.github.io/NetPulse-Server/ (Russian version under `/ru/`).

To build it locally:

```
pip install -r requirements-docs.txt
mkdocs serve
```

Start with `docs/manual/en/getting-started.md`. The server pages are under `server/`, the plugin guide under `plugins/`, and the wire protocol and code tables under `reference/`.

## Layout

| Path | What |
|---|---|
| `src/NetPulse.Common` | netstandard2.1 / C# 9 protocol, serialization, typed operations. Also compiled inside Unity. |
| `src/NetPulse.Server` | the host (exe) |
| `src/NetPulse.Plugins` | plugin SDK (reference this from your plugin) |
| `plugins/NetPulse.Plugins.Sample` | sample plugin: authoritative score, repeating timer event, event rejection |
| `tools/NetPulse.TestClient` | console bot client |
| `tools/sync-common.ps1` | copies Common into the Unity package |
| `tests/` | xUnit tests (serialization, rooms, plugins, real UDP integration) |

## Build, test, run

```
dotnet build
dotnet test
dotnet run --project src/NetPulse.Server
```

Building the solution also copies the sample plugin into `src/NetPulse.Server/bin/<Config>/net10.0/plugins/NetPulse.Plugins.Sample/`, where the server loads it.

Try it with two bots (server must be running):

```
dotnet run --project tools/NetPulse.TestClient -- --bots 2 --room test --seconds 6
```

Flags: `--host`, `--port`, `--bots`, `--room`, `--plugin` (empty string for none), `--seconds`, `--app-id`, `--key`. Exit code 0 means the bots saw each other's events, plugin timer ticks and a server-side score.

Console commands while the server runs: `stats`, `plugins`, `reload <folder>`, `quit`.

## Configuration

`appsettings.json` (override with environment variables such as `NetPulse__Port=6000`, or command line `--NetPulse:Port=6000`):

| Key | Default | Meaning |
|---|---|---|
| `NetPulse:Port` | 5055 | UDP port (0 picks a random free port) |
| `NetPulse:ConnectionKey` | NetPulse | LiteNetLib connection key; clients must match |
| `NetPulse:MaxConnections` | 2000 | extra connections are rejected |
| `NetPulse:AppId` | empty | when set, clients must send the same AppId |
| `NetPulse:AppVersionCheck` | true | reject clients with a different protocol version |
| `NetPulse:TickRate` | 30 | transport update rate |
| `NetPulse:DisconnectTimeoutMs` | 10000 | silent peers are dropped after this |
| `NetPulse:RoomListIntervalMs` | 1000 | lobby delta interval |
| `NetPulse:EnableConsole` | true | read console commands from stdin |
| `NetPulse:Plugins:Directory` | plugins | relative to the server binary, or absolute |
| `NetPulse:Plugins:Default` | empty | plugin used by rooms that do not name one |
| `NetPulse:Plugins:Auth` | empty | name of the plugin that provides `IAuthPlugin` |
| `NetPulse:Plugins:HotReload` | true | watch the plugin folder |
| `NetPulse:Plugins:HookTimeoutMs` | 5000 | a hook (or plugin factory) that does not finish in time is failed |
| `NetPulse:Plugins:AuthTimeoutMs` | 5000 | timeout for `IAuthPlugin`; on expiry the client gets `AuthenticationFailed` |
| `NetPulse:PeerQueueLimit` | 256 | max queued-but-unprocessed messages per peer across lobby and rooms; a peer above it is disconnected with `Overloaded` and the message is dropped |
| `NetPulse:RoomQueueCapacity` | 4096 | hard bound on peer messages queued in one room (or the lobby); server-internal work (timers, `Enqueue`, shutdown) is exempt. When it is full the message is dropped and the peer with the most queued messages is disconnected, not necessarily the poster |
| `NetPulse:ShutdownTimeoutMs` | 10000 | budget for closing rooms on shutdown |
| `NetPulse:OwnershipRequestTimeoutMs` | 5000 | how long a `Request`-mode ownership request waits for the owner |
| `NetPulse:Plugins:Settings:<Plugin>:<key>` | | passed to the plugin factory as `config` |

Metrics are published through `System.Diagnostics.Metrics` under the meter `NetPulse` (`netpulse.peers`, `netpulse.rooms`, `netpulse.msgs_in`, `netpulse.msgs_out`), so `dotnet-counters monitor --counters NetPulse` works.

## Deploy

```
dotnet publish src/NetPulse.Server -c Release -o publish
dotnet publish plugins/NetPulse.Plugins.Sample -c Release -o publish/plugins/NetPulse.Plugins.Sample
```

Open the UDP port in the firewall and run `publish/NetPulse.Server` (or `dotnet NetPulse.Server.dll`). Put your own `appsettings.json` next to it, or set environment variables. For a service, run it under systemd or as a container with `-p 5055:5055/udp`; set `NetPulse:EnableConsole=false` when there is no stdin.

## Behaviour notes

- Every room runs on its own async fiber: a single consumer that awaits each queued item before starting the next, with a custom synchronization context so every continuation after an `await` returns to the same fiber. Room logic and plugin code of one room never run in parallel (they may interleave at `await` points), so plugin authors need no locks. Flood protection is per peer: each peer may have `NetPulse:PeerQueueLimit` (256) messages queued and unprocessed; beyond that it is disconnected with the reason `Overloaded` (counted in `netpulse.peers_overloaded`). `RoomQueueCapacity` is only a last-resort bound. Lobby and matchmaking run on a separate fiber, and slow work in one room never delays other rooms, the lobby or the network thread.
- Event cache: `AddToRoomCache` events are replayed to late joiners and removed when their sender leaves; `AddToRoomCacheGlobal` events stay. `ObjectDestroy` with a `ViewId` removes the cached events of that view id.
- Properties: a `null` value deletes a key. `ExpectedProperties` makes a set conditional (compare and set); a mismatch returns `PropertyCasFailed`. Reserved room keys `_isOpen`, `_isVisible`, `_maxPlayers` (see `RoomPropertyKeys`) are master-only.
- Player TTL: with `PlayerTtlMs > 0` a disconnected player stays as an inactive actor and can rejoin with the same UserId. `EmptyRoomTtlMs > 0` keeps an empty room alive for that long.
- A raise-event request is answered only when it carries a non-zero request id, or when it fails.
- Unknown custom types (codes 100-199, such as Unity vectors) are stored and forwarded as `OpaqueValue`; the server never needs to understand them.

## Writing a plugin, step by step

1. Create a class library targeting `net10.0` and reference the SDK. Inside this repository reference the projects, and keep them out of your output so the host's copies are used:

   ```xml
   <ProjectReference Include="..\..\src\NetPulse.Plugins\NetPulse.Plugins.csproj" Private="false" />
   <ProjectReference Include="..\..\src\NetPulse.Common\NetPulse.Common.csproj" Private="false" />
   ```

   `NetPulse.Plugins`, `NetPulse.Common` and `Microsoft.Extensions.Logging.Abstractions` always come from the host.

2. Write a factory and a plugin. Everything is asynchronous; a hook is a `ValueTask` method that must call `Continue()`, `Fail(message)` or (where offered) `Cancel()` before it returns:

   ```csharp
   public sealed class MyFactory : IPluginFactory
   {
       public ValueTask<PluginCreateResult> CreateAsync(IPluginHost host, string pluginName, IReadOnlyDictionary<string, string> config, CancellationToken ct) =>
           ValueTask.FromResult(PluginCreateResult.Ok(new MyPlugin(host)));
   }

   public sealed class MyPlugin : RoomPlugin
   {
       public MyPlugin(IPluginHost host) : base(host) { }

       public override async ValueTask OnBeforeJoinAsync(IBeforeJoinCallInfo info, CancellationToken ct)
       {
           var allowed = await CheckExternalServiceAsync(info.UserId, ct);

           if (!allowed)
           {
               info.Fail("Not allowed");
               return;
           }

           info.Continue();
       }
   }
   ```

   A hook that returns without a decision is logged as an error and treated as `Fail`. A thrown exception, including one thrown after an `await`, goes to `OnUnhandledExceptionAsync`, the call fails, and the room keeps running. `Defer()` no longer exists: use `await`.

3. Threading rules:
   - After an `await` your code is back on the room fiber, so touching `Host.Actors`, `Host.RoomProperties` and your own fields is safe. Do not use `ConfigureAwait(false)` in plugin code that touches room state.
   - A slow hook stalls only its own room. While a hook awaits I/O the room handles nothing else, because message order matters more than latency. Other rooms, the lobby and the network thread are unaffected.
   - Every hook has a timeout (`HookTimeoutMs`) and receives a `CancellationToken` that fires on expiry. On timeout the call fails with a logged warning and any later `Continue()` is ignored.
   - Work whose result does not influence the call (analytics, webhooks, saving a match result) belongs in `Host.RunInBackground(async ct => ...)`. It runs off the fiber and is cancelled when the room closes; to touch room state afterwards call `Host.Enqueue(...)` or one of the `Host` methods, which marshal back to the fiber.
   - `Host.HttpAsync(request, ct)` goes through `IHttpClientFactory`; when you `await` it inside a hook, you are back on the fiber.
   - `Host.CreateTimer(due, period, async ct => ...)` is backed by `PeriodicTimer`; callbacks run on the fiber and a tick never starts while the previous callback is still running. The `Action` overload remains.

4. Add `plugin.json` next to the dll and copy both into `plugins/<Name>/`:

   ```json
   { "name": "MyPlugin", "assembly": "MyPlugin.dll", "factory": "MyNamespace.MyFactory" }
   ```

   Add `"auth": "MyNamespace.MyAuth"` and set `NetPulse:Plugins:Auth` to the plugin name if the type implements `IAuthPlugin` (`Task<AuthResult> AuthenticateAsync(AuthRequest, CancellationToken)`, subject to `AuthTimeoutMs`).

5. Pick the plugin per room with `RoomOptions.Plugin = "MyPlugin"`, or for all rooms with `NetPulse:Plugins:Default`.

6. Overwrite the files while the server runs: the folder is reloaded (debounced), new rooms use the new version, running rooms finish on the old one, which is unloaded when its last room closes. `reload <folder>` forces it. A failed reload keeps the previous version. On shutdown the server stops accepting connections, runs `OnBeforeCloseRoomAsync` / `OnCloseRoomAsync` for every room (within `ShutdownTimeoutMs`), disposes the plugins (`DisposeAsync`) and unloads their load contexts.

Hooks: `OnCreateRoomAsync`, `OnBeforeJoinAsync`, `OnJoinAsync`, `OnLeaveAsync`, `OnRaiseEventAsync`, `OnBeforeSetPropertiesAsync`, `OnSetPropertiesAsync`, `OnBeforeCloseRoomAsync`, `OnCloseRoomAsync`, `OnUnhandledExceptionAsync`. `IRaiseEventCallInfo.Request` is mutable, so a plugin can rewrite an event before it is delivered. If `Request.Data` is an `OpaqueValue` the plugin can parse it with `NetReader`.

## Network object ownership

The server keeps a registry of network objects per room and enforces ownership, so a client cannot move, destroy or steal someone else's objects.

- `ObjectInstantiate` (240) registers an object. The sender must be the creator: `viewId / 1000 == senderActorId`. Set `RaiseEventRequest.OwnershipMode` (`Fixed`, `Takeover`, `Request`) on that event; the default is `Fixed`.
- Scene objects use view ids 1-999 and belong to the master client by default (owner 0 means "the master"). The master declares their modes once with `RegisterSceneObjects`; undeclared scene objects are `Fixed`.
- `StateSync` (243) is accepted only from the current owner. `ObjectDestroy` (241) only from the owner or the master client. RPCs (242) stay open to everyone.
- `TransferOwnership`: `Fixed` never changes; `Takeover` lets any actor take it (new owner = sender) while the owner or the master can assign anyone; `Request` lets only the owner or the master assign.
- `RequestOwnership` for a `Request`-mode object is forwarded to the owner as the event `OwnershipRequested` (237). The owner approves by calling `TransferOwnership` to the requester; ignoring it lets the request time out with no change. Every change is broadcast as `OwnershipChanged`, and late joiners get the current owner.
- Rejected messages are dropped, the sender gets `Forbidden` (or an `ErrorInfo` event when the request had no request id, at most once a second), a rate-limited warning is logged, and `netpulse.ownership_violations` counts them. Nobody is disconnected for this.
- When an actor leaves for good it loses only the objects it currently owns (they and their cached events are removed); objects it created but handed over stay. Scene objects it owned return to the master. Inactive (TTL) players keep everything until they really leave.
- Plugins can read the registry (`Host.GetNetworkObject(viewId)`, `Host.NetworkObjects`) and veto changes in `OnBeforeTransferOwnershipAsync`.

## Custom authentication

By default any client is accepted. To validate clients, set `NetPulse:Plugins:Auth` to the name of a plugin that provides an `IAuthPlugin`; the server then uses the `userId` and `nickName` the plugin returns instead of the client-sent ones. If `Plugins:Auth` is set but the plugin cannot load, the server refuses to start. A rejected or timed-out (`Plugins:AuthTimeoutMs`) client gets `AuthenticationFailed` and is disconnected.

`plugins/NetPulse.Plugins.AuthSample` (plugin name `AuthSample`) is disabled until you configure it. Example configuration (in `appsettings.json`, or as environment variables such as `NetPulse__Plugins__Auth=AuthSample`):

```json
{
  "NetPulse": {
    "Plugins": {
      "Auth": "AuthSample",
      "AuthTimeoutMs": 5000,
      "Settings": {
        "AuthSample": {
          "Mode": "Hmac",
          "Secret": "change-me-long-random-string"
        }
      }
    }
  }
}
```

**Mode `Hmac` (offline).** The token is `base64url(userId|nickName|expiryUnix)` + `.` + `base64url(HMACSHA256(secret, payloadBytes))`. The server checks the signature in constant time and the expiry. Generate a test token with the bot client:

```
dotnet run --project tools/NetPulse.TestClient -- --make-token --secret change-me-long-random-string --user user42 --nick Alice --ttl-minutes 60
dotnet run --project tools/NetPulse.TestClient -- --bots 1 --token <token>
```

Your backend can mint tokens with the same recipe (see `HmacTokens.Create`). The secret is never logged.

**Mode `Http`.** Set `"Mode": "Http"` and `"Url": "https://auth.example/verify"`. The server POSTs the `AuthRequest` as camelCase JSON (`appId`, `userId`, `nickName`, `authToken`, `authData`, `remoteEndpoint`) through `IHttpClientFactory` and expects `{ "ok": true, "userId": "...", "nickName": "...", "message": "", "data": {} }`. `ok: false`, a non-2xx status or a malformed body reject the client; the call honours `AuthTimeoutMs` and the cancellation token.

**Unity client.** Pass the token in the connection options, for example `Pulse.Connect(new ConnectionOptions { AuthToken = token })`; the client sends it as `AuthenticateRequest.AuthToken`. After a successful login read the authoritative identity from the response (`UserId`, `NickName`).

To write your own provider, implement `IAuthPlugin`; a public constructor taking `(IReadOnlyDictionary<string,string> config, IAuthPluginContext context)` receives the plugin settings and an `HttpAsync` helper, and `plugin.json` names the type in `"auth"` (a plugin may have only `auth`, without `factory`).
