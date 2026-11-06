# Getting started

This page takes you from nothing to a Unity client sitting in a room on your own server. Each step links to the detailed pages.

## Requirements

- The .NET 10 SDK to build and run the server.
- Unity 2021.3 or newer (6000.x works too) for the client.
- Git access to the NetPulse repositories. They are private for now.

## 1. Run the server

Clone the server repository, then build and start it:

```bash
git clone https://github.com/Klobiroff/NetPulse-Server.git
cd NetPulse-Server
dotnet build
dotnet run --project src/NetPulse.Server
```

The build also copies the sample plugin into the server's `plugins` folder. The server logs the port it listens on:

```text
info: NetPulse.Server.Plugins.PluginManager[0] Loaded plugin NetPulse.Plugins.Sample v1 from NetPulse.Plugins.Sample
info: NetPulse.Server.Networking.NetPulseServer[0] NetPulse server listening on UDP 5055
info: NetPulse.Server.Commands.ConsoleCommandService[0] Console commands: stats, plugins, reload <folder>, quit
```

The default port is UDP 5055. If a firewall sits between the server and the clients, open that UDP port. See [Running the server](server/running.md) for the details.

To check the server before you touch Unity, run two test bots against it in a second terminal:

```bash
dotnet run --project tools/NetPulse.TestClient -- --bots 2 --room test --seconds 6
```

The bots join the room `test`, exchange events and print `OK` when everything worked.

## 2. Install the Unity package

The package lives in the Unity repository under `Packages/com.netpulse.unity`. Open **Window > Package Manager**, press **+**, choose **Add package from git URL** and paste:

```text
https://github.com/Klobiroff/NetPulse-Unity.git?path=Packages/com.netpulse.unity
```

Unity runs git for you, so the machine needs git installed and credentials that can read the private repository.

!!! note "No git access?"
    Copy the folder `Packages/com.netpulse.unity` from a checkout of the Unity repository into the `Packages` folder of your project. Unity picks it up on the next domain reload. You can also use **Add package from disk** and select the `package.json` inside that folder.

More detail is on the [Installation](unity/installation.md) page.

## 3. Configure the connection

Open **Project Settings > NetPulse** (or **NetPulse > Create Settings Asset**). Unity creates `Assets/Resources/NetPulseSettings.asset`. Set the host and port of your server. Leave the AppId empty unless you set `NetPulse:AppId` on the server. See [Settings and editor](unity/settings-and-editor.md).

## 4. Connect and join a room

Add this script to any GameObject in a scene:

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class Demo : MonoBehaviour
{
    private async void Start()
    {
        Pulse.NickName = "Alice";
        await Pulse.Connect();
        var room = await Pulse.JoinOrCreateRoom("arena", new RoomOptions { MaxPlayers = 4 });
        Debug.Log($"Joined {room.Name}, players: {room.PlayerCount}");
    }
}
```

Press Play. The server log shows `Creating room arena for ...` for the first player. Run a second copy (a build, or a Multiplayer Play Mode instance) and the player count goes up.

A failed operation throws `NetPulseException` with the server's `ReturnCode`. The codes are listed in [Codes and keys](reference/codes.md).

## Where to go next

- [Concepts](concepts.md) explains rooms, peers, network objects and ownership.
- [Connecting](unity/connecting.md) and [Matchmaking](unity/matchmaking.md) cover the lobby and the different ways to join.
- [Network objects](unity/network-objects.md) and [RPC](unity/rpc.md) cover gameplay synchronization.
- [Configuration](server/configuration.md) lists every server option, and [Writing a plugin](plugins/writing-a-plugin.md) shows how to add server logic.
