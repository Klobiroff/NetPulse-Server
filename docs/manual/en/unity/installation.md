# Installation

The client is the Unity package `com.netpulse.unity` (version 0.3). It supports Unity 2021.3 LTS through Unity 6000.x, uses plain C# 9 and runs on the main thread, and it works with both Mono and IL2CPP. You also need a running NetPulse server; see [getting started](../getting-started.md).

## Install with the Package Manager

1. Open `Window > Package Manager`.
2. Click `+` and choose `Install package from git URL...`.
3. Paste this URL and confirm:

```text
https://github.com/Klobiroff/NetPulse-Unity.git?path=Packages/com.netpulse.unity
```

The repository is private for now, so Unity needs git access. Install a git client, make sure `git` is on your `PATH`, and sign in to GitHub with an account that can read the repository (Git Credential Manager does this on Windows and macOS). If the Package Manager reports an authentication error, run `git ls-remote https://github.com/Klobiroff/NetPulse-Unity.git` in a terminal to check that your credentials work.

You can add the same line to `Packages/manifest.json` instead:

```json
{
  "dependencies": {
    "com.netpulse.unity": "https://github.com/Klobiroff/NetPulse-Unity.git?path=Packages/com.netpulse.unity"
  }
}
```

To pin a version, append `#` and a tag, branch or commit hash to the URL.

## Copy the folder instead

If git access is a problem, copy the `com.netpulse.unity` folder into your project's `Packages` folder. Unity treats it as an embedded package. Copy the whole folder including the `.meta` files, because the source generator DLL in `Runtime/Analyzers` relies on its import settings. As an alternative, use `Add package from disk...` in the Package Manager and select the folder's `package.json`.

## Assemblies

The package defines three assemblies: `NetPulse.Runtime`, `NetPulse.Common` (the protocol and serialization code, which is synced from the server repository, so do not edit it) and `NetPulse.LiteNetLib` (the vendored transport, LiteNetLib 1.3.5). Scripts in the default `Assembly-CSharp` reference them automatically.

If your code lives in its own assembly definition, add references to `NetPulse.Runtime` and `NetPulse.Common`. The source generator runs for every assembly that references `NetPulse.Runtime`, and the code it writes uses types from `NetPulse.Common`.

## Create the settings asset

Open `Project Settings > NetPulse`, or choose `NetPulse > Create Settings Asset`. Unity creates `Assets/Resources/NetPulseSettings.asset`. Set the host, the port (UDP 5055 by default), the `AppId` and the `AppVersion`. If the server is configured with an `AppId`, the client's `AppId` must equal it. The `AppVersion` is your own build version string; the server does not check it. Every field is described in [settings and editor](settings-and-editor.md).

Without the asset, `Pulse.Connect()` still works with built-in defaults (host `127.0.0.1`, port 5055), which is enough for a local test.

## Input System

The package itself reads no input. The Basic sample does, and its scripts compile against whichever input backend your project uses. They use the Input System package when `ENABLE_INPUT_SYSTEM` is defined and the legacy Input Manager otherwise. If `Project Settings > Player > Active Input Handling` is set to `Input System Package (New)` or `Both`, install the `com.unity.inputsystem` package, or the sample scripts will not compile.

## The Basic sample

The sample is an IMGUI demo. It connects, lists rooms, creates and joins rooms, and spawns a player with generated RPCs, a `SyncVar` health value, a typed chat message, a typed room score, tick-based movement, master-only scene loading and a shared Takeover cube.

1. Open `Window > Package Manager`, select NetPulse, open the `Samples` tab and import `Basic`.
2. Choose `NetPulse > Samples > Build Basic Sample Scene`. This creates the settings asset if it is missing, a player prefab, the prefab registry and two scenes (`BasicSample` and `BasicSampleArena`) under `Assets/NetPulse Basic Sample`, and adds both scenes to Build Settings.
3. Start a server, open `BasicSample` and press Play. Run a second instance (a standalone build, or a virtual player from Unity's Multiplayer Play Mode) to see two players.

Controls in the sample: `WASD` moves, `Space` sends an RPC, `H` damages your own `SyncVar` health, and `E` takes the shared cube, which you then move with the arrow keys.

## Running the package tests

The package includes tests in `Tests/Runtime` (serialization, the client on a fake transport, messages, typed keys, ticks, async helpers, network scenes, stats, `SyncVar`, generated RPCs, hooks and ownership) and in `Tests/Editor`. They are visible in the Test Runner when the package is listed under `testables` in `Packages/manifest.json`. A package that is embedded in `Packages` is already testable.

The ownership tests create GameObjects and use `[UnityTest]`, so run them from the Test Runner in Play Mode.

## Next steps

Continue with [connecting](connecting.md), then [matchmaking](matchmaking.md). If you know Photon, [migrating from PUN](../migrating-from-pun.md) maps the concepts you already have.
