# Writing a plugin

This walkthrough builds a small plugin from an empty project to one that the server has loaded. The plugin greets each player who joins and masks banned words in chat messages.

## 1. Create the project

You need the .NET 10 SDK and a checkout of the server repository, because the plugin references the `NetPulse.Plugins` and `NetPulse.Common` projects from it.

```bash
dotnet new classlib -n GreeterPlugin -f net10.0
cd GreeterPlugin
rm Class1.cs
```

Replace the contents of `GreeterPlugin.csproj`. Change `path\to\NetPulse_Server` to the location of your checkout:

```xml title="GreeterPlugin.csproj"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="path\to\NetPulse_Server\src\NetPulse.Plugins\NetPulse.Plugins.csproj" Private="false" />
    <ProjectReference Include="path\to\NetPulse_Server\src\NetPulse.Common\NetPulse.Common.csproj" Private="false" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.14" PrivateAssets="all" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`Private="false"` keeps the SDK assemblies out of your output. The host always supplies its own copies of `NetPulse.Plugins`, `NetPulse.Common`, `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`, and ignores any copy in the plugin folder. The logging package is referenced only so the plugin compiles.

If you cannot reference the projects, build the repository once and reference the dlls instead:

```xml
<ItemGroup>
  <Reference Include="NetPulse.Plugins">
    <HintPath>path\to\NetPulse_Server\src\NetPulse.Plugins\bin\Release\net10.0\NetPulse.Plugins.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="NetPulse.Common">
    <HintPath>path\to\NetPulse_Server\src\NetPulse.Common\bin\Release\netstandard2.1\NetPulse.Common.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

## 2. Write the factory and the plugin

A plugin has two classes. The factory reads the configuration and creates one plugin object per room. The plugin derives from `RoomPlugin` and overrides the hooks it needs.

```csharp title="GreeterPlugin.cs"
using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;

namespace GreeterPlugin;

public sealed class GreeterFactory : IPluginFactory
{
    public ValueTask<PluginCreateResult> CreateAsync(IPluginHost host, string pluginName, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        var greeting = config.TryGetValue("Greeting", out var value) ? value : "Welcome";
        var banned = config.TryGetValue("BannedWords", out var raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        return ValueTask.FromResult(PluginCreateResult.Ok(new GreeterPlugin(host, greeting, banned)));
    }
}

public sealed class GreeterPlugin : RoomPlugin
{
    public const byte ChatEvent = 20;
    public const byte GreetingEvent = 60;

    private readonly string _greeting;
    private readonly string[] _banned;

    public GreeterPlugin(IPluginHost host, string greeting, string[] banned)
        : base(host)
    {
        _greeting = greeting;
        _banned = banned;
    }

    public override ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct)
    {
        var actorId = info.Actor.ActorId;
        var text = $"{_greeting}, {info.Actor.NickName}!";

        Host.Enqueue(() => Host.BroadcastEvent(
            GreetingEvent,
            text,
            ReceiverGroup.Targets,
            new[]
            {
                actorId
            }));

        Host.Logger.LogInformation("Greeted {Nick} in room {Room}", info.Actor.NickName, Host.RoomName);
        info.Continue();
        return default;
    }

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        if (info.Request.EventCode == ChatEvent && info.Request.Data is string message)
        {
            info.Request.Data = Mask(message);
        }

        info.Continue();
        return default;
    }

    private string Mask(string message)
    {
        foreach (var word in _banned)
        {
            message = message.Replace(word, new string('*', word.Length), StringComparison.OrdinalIgnoreCase);
        }

        return message;
    }
}
```

What happens here:

- `CreateAsync` returns a `PluginCreateResult`. Use `PluginCreateResult.Failed("reason")` to refuse to create the room's plugin; the client then gets `PluginReportedError`.
- `OnJoinAsync` runs after the player has been added to the room but before the server sends the join response. A message sent from inside the hook would reach the client before it knows it is in the room, so the plugin uses `Host.Enqueue` to send the greeting after the join has finished.
- Every hook must call `Continue()`, `Fail(message)` or, where offered, `Cancel()` before it returns. A hook that returns without deciding is logged as an error and counts as `Fail`.
- `info.Request` is mutable. Changing `Data` in `OnRaiseEventAsync` changes what the other players receive.
- The hooks are `ValueTask` methods. When there is nothing to await, return `default`.

## 3. Add the manifest

Create `plugin.json` next to the project file. The csproj copies it to the output.

```json title="plugin.json"
{
  "name": "GreeterPlugin",
  "assembly": "GreeterPlugin.dll",
  "factory": "GreeterPlugin.GreeterFactory"
}
```

`name` is what rooms and the configuration use. `factory` is the full name of the factory class, namespace included.

## 4. Build and install

```bash
dotnet publish -c Release -o out
```

Copy the result into a folder named after the plugin, inside the server's plugin directory:

=== "Running from source"

    ```bash
    mkdir -p path/to/NetPulse_Server/src/NetPulse.Server/bin/Debug/net10.0/plugins/GreeterPlugin
    cp out/GreeterPlugin.dll out/GreeterPlugin.pdb out/plugin.json path/to/NetPulse_Server/src/NetPulse.Server/bin/Debug/net10.0/plugins/GreeterPlugin/
    ```

=== "Published server"

    ```bash
    mkdir -p publish/plugins/GreeterPlugin
    cp out/GreeterPlugin.dll out/GreeterPlugin.pdb out/plugin.json publish/plugins/GreeterPlugin/
    ```

If the server is already running with hot reload on, it picks up the new folder by itself within a second.

## 5. Turn it on

Rooms use the plugin when `RoomOptions.Plugin` names it, or when it is the default plugin. To make it the default and pass settings, add this to `appsettings.json`:

```json
{
  "NetPulse": {
    "Plugins": {
      "Default": "GreeterPlugin",
      "Settings": {
        "GreeterPlugin": {
          "Greeting": "Hello",
          "BannedWords": "darn,heck"
        }
      }
    }
  }
}
```

Environment variables work as well: `NetPulse__Plugins__Default=GreeterPlugin`.

From Unity, a single room can ask for it:

```csharp
var room = await Pulse.JoinOrCreateRoom("lobby-1", new RoomOptions { MaxPlayers = 8, Plugin = "GreeterPlugin" });
```

## 6. Check that it loaded

Start the server and look for the load message:

```text
info: NetPulse.Server.Plugins.PluginManager[0] Loaded plugin GreeterPlugin v2 from GreeterPlugin
```

Run `plugins` in the server console to list it. Then join a room. The server log shows `Greeted Alice in room lobby-1`, and the joining client receives event 60 with the greeting text. Chat messages sent as event 20 reach the other players with the banned words masked.

If the plugin does not load, see the troubleshooting list in [Operations](../server/operations.md#a-plugin-is-not-found-or-fails). To test hooks without a game client, see [Testing](testing.md).

## Next steps

- [Hooks](hooks.md) describes every hook and the rules for Continue, Fail and Cancel.
- [Host API](host-api.md) lists everything the plugin can do to its room.
- [Timers, HTTP and background work](timers-http-background.md) covers work that happens outside a hook.
