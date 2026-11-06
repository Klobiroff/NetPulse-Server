# Operations

This page covers what you use while the server runs: console commands, metrics, logs and a checklist for common problems.

## Console commands

The console reads one command per line from standard input while `NetPulse:EnableConsole` is `true`.

| Command | What it does |
|---|---|
| `stats` | Prints uptime, counters, loaded plugins and open rooms. |
| `plugins` | Lists loaded plugins and how many rooms use each. |
| `reload <folder>` | Reloads the plugin in the given subfolder of the plugin directory. Prints `reloaded` or `reload failed`. |
| `quit` or `exit` | Stops the server gracefully. |

Anything else prints `Unknown command. Available: stats, plugins, reload <folder>, quit`.

### stats

```text
uptime      0.00:12:31
peers       2
rooms       1
msgs in/out 1542 / 3120
bytes in/out 84210 / 160388
plugin errs 0
plugin      NetPulse.Plugins.Sample v1 (rooms: 1)
room        test 2/2 open=True
```

`msgs` and `bytes` count messages the server received and sent since it started. `plugin errs` counts exceptions thrown by plugin code (see [Hooks](../plugins/hooks.md#errors-and-timeouts)). Hook timeouts are logged but not counted there. The `rooms: 1` after a plugin is the number of rooms currently running on it. The room lines include rooms that are hidden from the lobby list.

### plugins

```text
NetPulse.Plugins.Sample v1 leases=1
```

`leases` is the number of rooms that use this plugin version. During a [hot reload](../plugins/hot-reload.md) the old version stays loaded until its lease count drops to zero.

### reload

`reload MyPlugin` takes the name of the folder, not the plugin name from `plugin.json`. If the folder no longer exists, the plugin is unloaded and the command still prints `reload failed`, because there is nothing to load.

## Metrics

The server publishes its metrics through `System.Diagnostics.Metrics` under the meter name `NetPulse`.

| Instrument | Kind | Meaning |
|---|---|---|
| `netpulse.peers` | gauge | Connected peers. |
| `netpulse.rooms` | gauge | Rooms that exist. |
| `netpulse.msgs_in` | counter | Messages received from clients. |
| `netpulse.msgs_out` | counter | Messages sent to clients. |
| `netpulse.peers_overloaded` | counter | Peers disconnected for exceeding the queue limits (reason `Overloaded`). |
| `netpulse.ownership_violations` | counter | Network object operations rejected by ownership validation. |

Watch them live with the `dotnet-counters` tool:

```bash
dotnet-counters monitor --counters NetPulse --name NetPulse.Server
```

`dotnet-counters` and OpenTelemetry collectors can read any `Meter`. The server ships no exporter of its own; to send these metrics to a monitoring system you add the exporter in your own host code.

## Logs

The server writes single-line messages to standard output through the standard `Logging` configuration. There is no log file; capture the output with your service manager (`journalctl -u netpulse`, a Docker log driver, or a redirect).

```text
info: NetPulse.Lobby[0] Creating room arena for user42
info: NetPulse.Room.arena[0] Room arena closed
```

The category tells you where a message came from: `NetPulse.Lobby`, `NetPulse.Room.<room name>`, `NetPulse.Flood`, and the class names of the server components. Plugins log through `Host.Logger`, which uses the room's category.

Messages worth knowing:

| Level | Message | Meaning |
|---|---|---|
| Warning | `Disconnecting peer N (room X): exceeded its limit of 256 queued messages` | A client sent faster than the room could process. See `PeerQueueLimit`. |
| Warning | `Queue of room X is full; dropping a message from peer N` | The room queue hit `RoomQueueCapacity`. |
| Warning | `Malformed packet from peer N: ...` | A packet could not be parsed. Usually a client or protocol mismatch. |
| Warning | `Rejected object operation from actor N in room X: ...` | Ownership validation refused a message. Logged at most once a second per actor. |
| Warning | `Plugin hook P timed out after 00:00:05 in room X` | A hook exceeded `HookTimeoutMs`. |
| Error | `Exception in plugin code of room X` | A hook, timer or enqueued action threw. |
| Error | `Plugin hook returned without calling Continue/Fail in room X; treating as Fail` | A hook finished without making a decision. |
| Warning | `Plugin hook H was already completed as ...; ignoring ...` | A hook called `Continue`, `Fail` or `Cancel` twice, or after a timeout. |
| Error | `Failed to load plugin from F; keeping previous version if any` | A load or reload failed. The exception follows. |
| Warning | `Authentication plugin timed out for peer N` | `IAuthPlugin` exceeded `AuthTimeoutMs`. |
| Warning | `Not all rooms closed within 00:00:10` | Shutdown ran out of time. |

Connection and disconnection messages are written at `Debug` level. Set `Logging:LogLevel:Default` to `Debug` to see them.

## Troubleshooting

### Clients cannot connect

- Check that the server logged `listening on UDP <port>` and that the port matches the client settings.
- Allow inbound UDP on that port in every firewall and cloud security group. See [Deployment](deployment.md#open-the-port).
- Connection key mismatch: the server silently rejects a client whose key differs from `NetPulse:ConnectionKey`.
- `NetPulse:MaxConnections` reached: new connections are rejected. Compare with `netpulse.peers`.
- In Docker, publish the port as `5055:5055/udp`.

### Connecting works but authentication fails

| Code | Cause |
|---|---|
| `InvalidAppId` (-31) | The client's AppId differs from `NetPulse:AppId`. |
| `VersionMismatch` (-30) | The client speaks another protocol version. Update the package and the server together, or set `AppVersionCheck` to `false` to skip the check. |
| `AuthenticationFailed` (-32) | The authentication plugin rejected the client, timed out or failed. The server disconnects the client shortly afterwards. The message says why. |
| `NotAuthenticated` (-3) | The client sent another operation before authenticating. |

### A plugin is not found or fails

- `PluginNotFound` (-21) with `Plugin 'X' is not installed`: the room asked for a plugin that is not loaded. The name must equal the `name` in `plugin.json`, and the folder must contain that file. The same code with `provides no room plugin` means the manifest has no `factory`.
- No `Loaded plugin` line at startup: run with `Logging:LogLevel:Default` at `Debug`. A folder without `plugin.json` is logged as skipped.
- `Failed to load plugin from ...`: read the exception below it. Typical causes are a wrong `assembly` or `factory` name, or a missing dependency in the plugin folder.
- `PluginReportedError` (-20) on create or join, with a message such as `Plugin hook timed out`, `Plugin did not respond` or `Plugin error`: the hook failed. The server log has the details. See [Hooks](../plugins/hooks.md).

### Clients are disconnected with `Overloaded`

A peer sent more than `PeerQueueLimit` messages that the room had not processed yet. Either the client sends too many messages per second, or the room is slow because a plugin hook waits on I/O. Check `netpulse.peers_overloaded` and the warning lines above. Fix the slow hook first; raise the limit only if the traffic is legitimate.

### A room does not close or does not update

- `EmptyRoomTtlMs` keeps an empty room alive for that long, and `PlayerTtlMs` keeps a left player inactive. Both are room options set by the client.
- A plugin that cancels or fails `OnBeforeCloseRoomAsync` keeps an emptied room open. The server shutdown ignores that and closes it anyway.
- A hook that never finishes stalls its room until `HookTimeoutMs` expires. Other rooms keep working.

### A plugin change has no effect

Hot reload waits half a second after the last file change. New rooms get the new version, running rooms keep the old one. If the reload fails, the previous version stays. Run `reload <folder>` and read the log. See [Hot reload](../plugins/hot-reload.md).

### Players drop after about ten seconds

A peer that sends nothing is dropped after `NetPulse:DisconnectTimeoutMs` (10 seconds). On the client this usually means the connection was lost, or the game was paused (for example by a debugger breakpoint) long enough to miss the keep-alive messages.
