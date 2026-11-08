# NetPulse

NetPulse is a networking solution for Unity games. It has two parts: a .NET 10 server that you host yourself, and a Unity package (`com.netpulse.unity`) that works with Unity 2021.3 through 6000.x. The model will be familiar if you have used Photon PUN: players connect, sit in a lobby, create or join rooms, share room and player properties, call RPCs and synchronize networked objects.

Both parts are at version 0.4.

## What you get

On the server:

- Lobby and matchmaking: named rooms, random join with property filters, a live room list with deltas.
- Rooms that run on their own async fiber, so one slow room never delays the others, the lobby or the network thread.
- An event cache that replays state to late joiners, and player and empty-room time to live for rejoining.
- Server-side validation of network object ownership, so a client cannot move, destroy or steal objects it does not own.
- Flood protection per peer, an AppId and protocol version check, and optional custom authentication.
- Plugins written in C#: hooks for joins, events, property changes and room closing, timers, HTTP calls, background work and hot reload.
- Metrics through `System.Diagnostics.Metrics` and a small set of console commands.

In Unity:

- An async API (`await Pulse.Connect()`, `await Pulse.JoinOrCreateRoom(...)`) with callback and coroutine alternatives.
- RPCs, `SyncVar<T>`, typed messages and typed room and player keys, backed by a source generator and analyzers.
- Network objects with ownership, scene objects, network scenes, a network tick and an offline mode.
- Editor tools: a settings asset, a Hub window with a traffic profiler, and IL2CPP link.xml generation.

## How it compares to PUN

The concepts map closely, so most PUN code translates by renaming. The main differences are where the server runs and who enforces the rules. NetPulse is self-hosted: you run the server binary on your own machine and write server logic as plugins in C#, instead of relying on a hosted service. The server checks ownership itself, and the Unity API is built around `async`/`await` and generated code instead of string-based calls. The page [Migrating from PUN](migrating-from-pun.md) lists the concrete replacements.

## Where to go next

| If you want to | Read |
|---|---|
| Run the server and join a room in a few minutes | [Getting started](getting-started.md) |
| Understand rooms, peers, objects and ownership | [Concepts](concepts.md) |
| Write the client side | [Unity client](unity/installation.md) |
| Run, configure and deploy the server | [Server](server/running.md) |
| Add server-side logic | [Plugins](plugins/overview.md) |
| Write your own client or look up a code | [Reference](reference/protocol.md) |
| Move an existing PUN project | [Migrating from PUN](migrating-from-pun.md) |
| Fix a problem | [Troubleshooting](troubleshooting.md) and [Operations](server/operations.md) |
| See what changed | [Changelog](changelog.md) |

The source for both parts lives on [GitHub](https://github.com/Klobiroff/NetPulse-Server). The repository is private for now, so you need access to it to clone the server or install the Unity package from git.
