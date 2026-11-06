# Plugins overview

A plugin is a .NET class library that the server loads from a folder and runs inside your rooms. Use one when the server must make a decision that clients cannot be trusted with: validating events, keeping authoritative scores, checking an allow-list before a join, or driving a match with a timer.

## What a plugin can do

- Intercept room activity through hooks: create, join, leave, raise event, property changes, ownership transfers and room close. A hook can let the action through, change it, drop it or reject it. See [Hooks](hooks.md).
- Act on the room through the host API: broadcast events, set room and player properties, kick players, close the room. See [Host API](host-api.md).
- Run timers, call HTTP services and do background work. See [Timers, HTTP and background work](timers-http-background.md).
- Authenticate clients before they reach any room. See [Authentication](authentication.md).

## The pieces

| Piece | Role |
|---|---|
| `plugin.json` | Manifest in the plugin folder: name, assembly, factory type, optional auth type. |
| `IPluginFactory` | Created once per loaded plugin version. The server calls `CreateAsync` every time a room that uses the plugin is created. |
| `RoomPlugin` | The object that receives hooks. Each room gets its own instance, so its fields are per-room state. Derive from this base class and override only the hooks you need. |
| `IPluginHost` | What the plugin uses to act on its room. The base class exposes it as `Host`. |

Rooms pick a plugin by name through `RoomOptions.Plugin`. Rooms that do not name one use `NetPulse:Plugins:Default`, and if that is empty they run without a plugin.

## The room runs one thing at a time

Every room has its own queue and a single consumer, called a fiber. The fiber takes one item from the queue (a client message, a join, a leave, a timer start), runs it to completion, including every `await` inside it, and only then takes the next one. The lobby has its own fiber, and so does every other room.

This model has four consequences:

- Code of one room never runs in parallel with other code of that room. You need no locks around room state, fields of your plugin or `Host.RoomProperties`.
- After an `await` inside a hook you are back on the room fiber, so touching room state is still safe. Do not use `ConfigureAwait(false)` or `Task.Run` in code that touches room state.
- A slow hook stalls only its own room. While a hook waits on a database, the room handles nothing else, because the order of messages matters more than latency. Other rooms, the lobby and the network thread keep working. A hook that never finishes is cut off after `HookTimeoutMs`.
- Timer callbacks and other continuations are not queue items. They run on the same fiber, but they can run while a hook is suspended at an `await`. Two pieces of code never run at the same instant, yet they can interleave at await points.

Work whose result does not change the outcome of a call, such as analytics or saving a match result, belongs in `Host.RunInBackground` so that the room does not wait for it.

## Plugin versions and hot reload

The server loads a plugin into its own collectible load context. When you overwrite the files, new rooms use the new version and running rooms finish on the old one. See [Hot reload](hot-reload.md).

## The sample plugins

The repository contains two plugins under `plugins/`:

- `NetPulse.Plugins.Sample` keeps an authoritative score per player, broadcasts a timer tick every second and rejects one event code. It shows hooks, a factory with settings, and a timer.
- `NetPulse.Plugins.AuthSample` is an `IAuthPlugin` with an HMAC token mode and an HTTP mode. See [Authentication](authentication.md).

## Where to go next

Start with [Writing a plugin](writing-a-plugin.md), which builds a plugin from an empty project to a loaded one.
