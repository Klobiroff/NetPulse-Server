# Deployment

This page covers building a release folder, the two kinds of build, running the server as a service, and the plugin folder layout.

## Publish

```bash
dotnet publish src/NetPulse.Server -c Release -o publish
dotnet publish plugins/NetPulse.Plugins.Sample -c Release -o publish/plugins/NetPulse.Plugins.Sample
```

The first command publishes the server. The second publishes a plugin into the `plugins` folder inside the output. Publish your own plugins the same way, one folder per plugin.

The publish folder then looks like this:

```text
publish/
  NetPulse.Server.exe        (NetPulse.Server on Linux)
  NetPulse.Server.dll
  NetPulse.Common.dll
  NetPulse.Plugins.dll
  LiteNetLib.dll
  appsettings.json
  plugins/
    NetPulse.Plugins.Sample/
      plugin.json
      NetPulse.Plugins.Sample.dll
      NetPulse.Plugins.Sample.pdb
```

Edit `appsettings.json` in the publish folder, or leave it and use environment variables. Remember that the host reads `appsettings.json` from the working directory, see [Running the server](running.md#changing-settings).

## Framework-dependent or self-contained

=== "Framework-dependent"

    ```bash
    dotnet publish src/NetPulse.Server -c Release -o publish
    ```

    About 3 MB. The target machine needs the .NET 10 runtime (`Microsoft.NETCore.App`). The ASP.NET Core runtime is not required. Runtime updates come from the operating system's package manager.

=== "Self-contained"

    ```bash
    dotnet publish src/NetPulse.Server -c Release -r linux-x64 --self-contained -o publish
    ```

    Use the runtime identifier of the target, for example `win-x64`, `linux-x64` or `linux-arm64`. The output carries its own runtime and is around 80 MB, and the target machine needs nothing installed. You choose when to update the runtime by publishing again.

Plugins are plain class libraries and load in both modes. Publish them without a runtime identifier; they run on whichever runtime the server uses.

## Open the port

Clients connect over UDP to `NetPulse:Port` (5055 by default). Allow inbound UDP on that port, both in the machine's firewall and in any cloud security group or router in front of it. TCP rules do not apply.

=== "Linux (ufw)"

    ```bash
    sudo ufw allow 5055/udp
    ```

=== "Linux (firewalld)"

    ```bash
    sudo firewall-cmd --permanent --add-port=5055/udp
    sudo firewall-cmd --reload
    ```

=== "Windows"

    ```powershell
    New-NetFirewallRule -DisplayName "NetPulse" -Direction Inbound -Protocol UDP -LocalPort 5055 -Action Allow
    ```

## Plugin folder layout

The server scans `NetPulse:Plugins:Directory` (the `plugins` folder next to the binary by default). Each subfolder is one plugin and needs a `plugin.json`:

```json
{
  "name": "MyPlugin",
  "assembly": "MyPlugin.dll",
  "factory": "MyNamespace.MyFactory"
}
```

| Field | Meaning |
|---|---|
| `name` | The name rooms use in `RoomOptions.Plugin`, and the key under `NetPulse:Plugins:Settings`. |
| `assembly` | The dll in the same folder that holds the plugin. |
| `factory` | Full name of a class that implements `IPluginFactory`. Required for room plugins. |
| `auth` | Full name of a class that implements `IAuthPlugin`. See [Authentication](../plugins/authentication.md). |

A manifest needs `name`, `assembly` and at least one of `factory` or `auth`. A folder without `plugin.json` is skipped. The server never loads `NetPulse.Plugins.dll`, `NetPulse.Common.dll`, `Microsoft.Extensions.Logging.Abstractions.dll` or `Microsoft.Extensions.DependencyInjection.Abstractions.dll` from a plugin folder: it always hands the plugin its own copies. Other dependencies of your plugin load from its folder, so publish the plugin and copy the whole output.

## Run as a service

The server needs no stdin as a service, so turn the console off: set `NetPulse__EnableConsole=false`.

=== "Linux (systemd)"

    Create `/etc/systemd/system/netpulse.service`:

    ```ini
    [Unit]
    Description=NetPulse server
    After=network.target

    [Service]
    Type=simple
    User=netpulse
    WorkingDirectory=/opt/netpulse
    ExecStart=/opt/netpulse/NetPulse.Server
    Environment=NetPulse__EnableConsole=false
    Restart=on-failure
    RestartSec=5

    [Install]
    WantedBy=multi-user.target
    ```

    Then enable it:

    ```bash
    sudo systemctl daemon-reload
    sudo systemctl enable --now netpulse
    journalctl -u netpulse -f
    ```

    Make the binary executable (`chmod +x /opt/netpulse/NetPulse.Server`). For a framework-dependent build, set `ExecStart=/usr/bin/dotnet /opt/netpulse/NetPulse.Server.dll`. `WorkingDirectory` matters because `appsettings.json` is read from there. `systemctl stop` sends SIGTERM, which starts the graceful shutdown described in [Running the server](running.md#stopping-the-server).

=== "Windows"

    The server does not integrate with the Windows service control manager, so registering `NetPulse.Server.exe` directly with `sc.exe` does not work. Use a service wrapper such as NSSM:

    ```powershell
    nssm install NetPulse "C:\NetPulse\NetPulse.Server.exe"
    nssm set NetPulse AppDirectory "C:\NetPulse"
    nssm set NetPulse AppEnvironmentExtra NetPulse__EnableConsole=false
    nssm start NetPulse
    ```

    A scheduled task that runs at startup with the same working directory also works.

=== "Container"

    Publish the server, copy the publish folder into an image that has the .NET 10 runtime (or use a self-contained build), and publish the port as UDP:

    ```bash
    docker run -p 5055:5055/udp -e NetPulse__EnableConsole=false my-netpulse
    ```

    Without `/udp` Docker maps TCP and no client can connect.

## Updating

- Plugin only: overwrite the plugin files in place. With `HotReload` enabled the server loads the new version for new rooms and lets running rooms finish on the old one. See [Hot reload](../plugins/hot-reload.md).
- Server binary or configuration: stop the server, replace the files, start it again. Clients in rooms are disconnected from their rooms during the shutdown, so schedule the restart.
