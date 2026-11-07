---
name: integrating-netpulse
description: Use when adding multiplayer to a Unity game with NetPulse (com.netpulse.unity), converting a single-player or Photon PUN project to NetPulse, or writing NetworkBehaviour, RPC, SyncVar, room property or matchmaking code for a NetPulse game.
---

# Integrating NetPulse into a Unity game

## Overview

NetPulse looks like Photon PUN but its API is different. Never guess names from PUN: `PhotonNetwork`, `PhotonView`, `[PunRPC]`, `ConnectUsingSettings`, `CustomProperties` and Resources-based spawning do not exist here. Every name you need is in `reference.md` next to this file. If the package source is available (`Packages/com.netpulse.unity` or `Library/PackageCache/com.netpulse.unity*`), it wins over this skill.

## Integration recipe

1. **Settings.** `Project Settings > NetPulse` creates `Assets/Resources/NetPulseSettings.asset`: host, port (UDP 5055), AppId, AppVersion. On package 0.3.0 only, set `Interpolation Delay` on `NetworkTransform` to about `0.1` (its automatic value is 2 s there; 0.3.1 fixes it, leave it at `0`).
2. **Connect and join** from one `MonoBehaviour`: `await Pulse.Connect()`, then `await Pulse.JoinOrCreateRoom(name, new RoomOptions { MaxPlayers = n })`. `MaxPlayers` is a `byte`: cast an `int` field with `(byte)`. Catch `NetPulseException`. For random matchmaking use `Pulse.JoinRandomRoom(createOptions: new RoomOptions { ... })`: with `createOptions` the server creates a room when none matches (PUN's `JoinRandomOrCreateRoom`), so no fallback `CreateRoom` is needed.
3. **Player prefab.** Add `NetworkObject` to the root, plus `NetworkTransform` (and `NetworkAnimator` / `NetworkRigidbody` if used). Keep it under `Assets`: the prefab registry picks it up automatically. Spawn with `Pulse.Instantiate(prefabReference, pos, rot)` after joining.
4. **Scripts on networked objects** derive from `NetworkBehaviour`. Any class with `[NetRpc]` or `SyncVar<T>` is `partial` (`sealed partial` is fine).
5. **Authority.** Read input and move only when `IsSpawned && IsMine`. In `OnNetworkSpawned`, disable the local-only pieces (`CharacterController`, camera, audio listener) on remote copies.
6. **State** that others must see goes in `SyncVar<T>`. Only the owner writes it (or the master with `SyncPermission.Master`). Others ask the owner through an RPC: `Rpc.To(RpcTarget.Owner).TakeDamage(10)`.
7. **One-shot effects** (muzzle flash, sound) use `[NetRpc]` methods called as `Rpc.Flash()` or `Rpc.To(RpcTarget.All).Flash()`. Never call RPCs by string.
8. **Room-wide values** (score, round, timer) use `RoomKey<T>`: `room.Get`, `room.OnChanged`, and `room.SetAsync(key, value, expected)` in a retry loop when several clients may write.
9. **Run the server**: in the NetPulse server repo, `dotnet run --project src/NetPulse.Server` (UDP 5055). Test with a build plus the editor, or Multiplayer Play Mode.
10. **Verify** by compiling; the bundled analyzers report NP0001–NP0011 for wrong partial/RPC/SyncVar usage. Fix every NP diagnostic. If you cannot compile (no Unity, no package source), check by hand before handing over: every class that contains `SyncVar<`, `[NetRpc]`, `[NetSerializable]` or `: INetMessage` is declared `partial`, including small scene scripts like doors and pickups.

## Common mistakes

| Mistake | Fix |
|---|---|
| PUN names (`PhotonView`, `photonView.RPC`, `[PunRPC]`) | `NetworkObject`, `Rpc.Method(...)`, `[NetRpc]` |
| `Pulse.Instantiate("Name")` from Resources | Pass the prefab reference; the registry handles ids |
| Non-owner writes `SyncVar` (damage applied by the shooter) | RPC to `RpcTarget.Owner`, owner writes the value |
| Damage RPC to `All`, everyone subtracts health | Only the owner changes state; others receive the SyncVar |
| Remote copy runs input or `CharacterController` | Gate `Update` on `IsMine`, disable components when not mine |
| `room.Set` for contested counters | `SetAsync` with expected value and retry on `PropertyCasFailed` |
| Every client adds score for one kill | Only the client where `killerActorId == Pulse.LocalActorId` adds |
| `await op` assumed not to throw | Failed operations throw `NetPulseException`; catch it |
| `MaxPlayers = _intField` | `RoomOptions.MaxPlayers` is `byte`; cast it |
| Raycast hit on a child collider misses the `Health` | `hit.collider.GetComponentInParent<Health>()` |
| Code not compiled before handing over | Compile in Unity (or against the package) and fix every CS and NP error |

Full API cheat sheet and a compiled example (player, health, gun, shared score): `reference.md`.
