# Hot reload

With `NetPulse:Plugins:HotReload` enabled (the default), you can replace a plugin while the server runs. Players stay connected and running rooms are not interrupted.

## What happens when files change

1. The server watches the plugin directory, including subfolders. Any change inside a plugin folder starts a half-second timer for that folder. Further changes restart the timer, so copying several files produces one reload.
2. The server loads `plugin.json` and the assembly from that folder into a new isolated load context. The files are read into memory, so nothing stays locked and you can overwrite them again at any time.
3. If the load works, the new version takes the plugin's name. Rooms created from now on use it.
4. Rooms that already run the plugin keep the old version until they close. When the last of them has closed, the old version is unloaded.

The log shows `Loaded plugin <name> v<N> from <folder>`. The number is the server-wide load counter, so it rises with every load of any plugin.

If the load fails, for example because the dll is half copied or the manifest is invalid, the server logs `Failed to load plugin from <folder>; keeping previous version if any` and the previous version stays active. The next file change triggers another attempt.

Other cases:

- A new folder with a valid manifest loads as a new plugin.
- Deleting a plugin folder unloads the plugin. Running rooms finish on it, and rooms that ask for it afterwards get `PluginNotFound`.
- If the plugin is the one named in `NetPulse:Plugins:Auth`, a successful reload replaces the authentication plugin for new logins.

## Reloading by hand

The console command forces a reload of one folder, even with `HotReload` off:

```text
reload GreeterPlugin
```

It takes the folder name, prints `reloaded` or `reload failed` and writes the details to the log. Use `plugins` to check which plugins are loaded and how many rooms use each.

With `HotReload` set to `false` the server loads plugins only at startup, plus whatever you reload by hand.

## What to keep in mind

- State lives in the room's plugin object. A room never switches versions, so its fields stay consistent. A new room starts with fresh state.
- Each version has its own copy of static fields. Do not use statics to share data between rooms that may run different versions.
- Settings come from `appsettings.json` and environment variables when the server starts. Changing them needs a restart. A plugin reload gives the factory the same settings as before.
- Anything that outlives the room and references plugin code can keep the old version in memory: a thread you started, a timer you did not dispose, a static event subscription. Stop your own threads in `DisposeAsync` or `OnCloseRoomAsync`.
- A change in the contract between plugin and clients, for example a new meaning of an event code, affects rooms differently depending on the version they run. Roll such changes out with a restart if mixed versions would confuse clients.
- The shared assemblies (`NetPulse.Plugins`, `NetPulse.Common`) come from the server and cannot be hot reloaded. Updating the SDK needs a server restart and a rebuilt plugin.

## Disposal

When a room closes, the server calls `DisposeAsync` on its plugin object. Override it if the plugin holds resources.

```csharp
public override ValueTask DisposeAsync()
{
    _client.Dispose();
    return default;
}
```

On server shutdown the same happens for every room, and all load contexts are unloaded afterwards.
