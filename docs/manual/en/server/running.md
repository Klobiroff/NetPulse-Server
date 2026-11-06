# Running the server

The server is a .NET 10 console application built on the generic host. It listens on one UDP port, loads plugins from a folder next to the binary and reads console commands from standard input.

## Run from source

```bash
dotnet build
dotnet run --project src/NetPulse.Server
```

Building the solution also copies the sample plugin (and the AuthSample plugin) into `src/NetPulse.Server/bin/<Config>/net10.0/plugins/`, which is where the server looks for plugins.

## Run a published build

After [publishing](deployment.md), start the executable or the dll:

```bash
./NetPulse.Server
dotnet NetPulse.Server.dll
```

On Windows the executable is `NetPulse.Server.exe`.

## Startup output

A normal start prints one line per loaded plugin, the port and the list of console commands:

```text
info: NetPulse.Server.Plugins.PluginManager[0] Loaded plugin NetPulse.Plugins.Sample v1 from NetPulse.Plugins.Sample
info: NetPulse.Server.Networking.NetPulseServer[0] NetPulse server listening on UDP 5055
info: NetPulse.Server.Commands.ConsoleCommandService[0] Console commands: stats, plugins, reload <folder>, quit
```

The `v1` after the plugin name is a counter that the server increments on every plugin load, so the second plugin that loads shows `v2`. It is not a version from the plugin itself.

## Changing settings

The server reads `appsettings.json` and then overrides it from environment variables and the command line. Later sources win.

=== "appsettings.json"

    ```json
    {
      "NetPulse": {
        "Port": 6000,
        "AppId": "my-game"
      }
    }
    ```

=== "Environment variable"

    ```bash
    NetPulse__Port=6000 ./NetPulse.Server
    ```

=== "Command line"

    ```bash
    ./NetPulse.Server --NetPulse:Port=6000
    ```

Use a double underscore in environment variable names where the key has a colon (`NetPulse__Plugins__Auth`). Every option is listed in [Configuration](configuration.md).

!!! warning "appsettings.json comes from the working directory"
    The host looks for `appsettings.json` in the current working directory, not next to the binary. If you start the server from another folder, for example from a service manager, set the working directory to the folder that holds your `appsettings.json`, or use environment variables. The plugin folder is different: a relative `NetPulse:Plugins:Directory` is resolved against the folder of the server binary.

Setting `NetPulse:Port` to `0` picks a random free port. The log line `listening on UDP <port>` shows which one was chosen.

## Stopping the server

Press Ctrl+C, type `quit` (or `exit`) in the console, or send SIGTERM. The server then:

1. Stops accepting new connections.
2. Closes every room, running `OnBeforeCloseRoomAsync` and `OnCloseRoomAsync` for each, within `NetPulse:ShutdownTimeoutMs` (10 seconds by default). If rooms do not finish in time, the server logs `Not all rooms closed within ...` and continues.
3. Stops the network manager, disposes the plugins and unloads their load contexts.

Clients in a room receive an `ErrorInfo` event with the code `RoomClosed` before they are disconnected.

## When the server refuses to start

The process exits with an unhandled exception in two cases:

- The UDP port is already in use: `Could not bind UDP port 5055`.
- `NetPulse:Plugins:Auth` names a plugin that is not installed, or that fails to load or to construct: `Plugins:Auth is set to '...' but no authentication plugin could be loaded; refusing to start without authentication`.

The second case is deliberate. A server that was configured to authenticate never falls back to accepting everyone. See [Security](security.md).

## Console input

The console reads commands while `NetPulse:EnableConsole` is `true`. If standard input is closed or redirected from `/dev/null`, the console service simply ends and the server keeps running. Set the option to `false` for services and containers. The commands are described in [Operations](operations.md).

## Try it with the test client

The repository includes a bot client, `tools/NetPulse.TestClient`:

```bash
dotnet run --project tools/NetPulse.TestClient -- --bots 2 --room test --seconds 6
```

| Flag | Default | Meaning |
|---|---|---|
| `--host` | `127.0.0.1` | Server address |
| `--port` | `5055` | Server UDP port |
| `--bots` | `2` | Number of bot clients |
| `--room` | `test` | Room name |
| `--plugin` | `NetPulse.Plugins.Sample` | Plugin requested for the room; pass an empty string for none |
| `--seconds` | `6` | How long the bots exchange events |
| `--app-id` | none | AppId sent to the server |
| `--key` | `NetPulse` | LiteNetLib connection key |
| `--token` | none | Authentication token |
| `--make-token`, `--secret`, `--user`, `--nick`, `--ttl-minutes` | | Print an AuthSample token instead of running bots, see [Authentication](../plugins/authentication.md) |

Exit code 0 means the bots saw each other's events, the sample plugin's timer ticks and a server-side score, and that the sample plugin rejected event 99. The last check always runs, so with `--plugin ""` or a plugin other than the sample the client reports `FAILED` even when the server works. In that case read the `joined` lines instead: they show that connect, authenticate and join succeeded.
