# Scenes

Network scenes keep every player of a room in the same Unity scene. The master client calls `Pulse.LoadScene`, and every client, including players who join later, loads the same scene. Events that arrive during the load are held back and replayed in order afterwards.

```csharp
using NetPulse;
using UnityEngine;

public sealed class ArenaFlow : NetPulseCallbacks
{
    [SerializeField] private GameObject _propPrefab;

    public async void StartArena()
    {
        if (!Pulse.IsMasterClient)
        {
            return;
        }

        try
        {
            await Pulse.LoadScene("Arena");
        }
        catch (NetPulseException e)
        {
            Debug.LogError($"Could not load: {e.Message}");
        }
    }

    public override void OnNetworkSceneLoaded(string sceneName)
    {
        Debug.Log($"Scene ready: {sceneName}");

        if (Pulse.IsMasterClient)
        {
            Pulse.Instantiate(_propPrefab, Vector3.zero, Quaternion.identity);
        }
    }
}
```

Add every scene you load to `File > Build Settings`. If the scene cannot be loaded, an error is logged and the load operation fails with `InvalidState`.

## Loading

`Pulse.LoadScene(string sceneName)` and `Pulse.LoadScene(int buildIndex)` work like this:

- Only the master client may call them. Anyone else gets a warning and an operation that fails with `Forbidden`. Outside a room they fail with `InvalidState`. To let other players trigger a load, send the master a message or an RPC and let it call `LoadScene`.
- The master writes the room property `_scene` with the value `"{loadId}:{nameOrIndex}"`. The `loadId` grows with every load, so clients can tell a new request from one they already handled. Clients cannot write this property themselves.
- Every client, the master included, loads the scene asynchronously with a single-mode `SceneManager.LoadSceneAsync`.
- The returned operation completes on the master after the scene has loaded. On the other clients, `OnNetworkSceneLoaded` is the signal.

If a second `LoadScene` starts before the first one finishes, the newer load wins and the older operation fails with `NetPulseReturnCode.Cancelled`. The same happens when the client leaves the room or disconnects during a load. Awaiting such an operation throws `NetPulseException`.

## Auto sync

`Auto Sync Scene` in the settings (on by default, [settings and editor](settings-and-editor.md)) turns the loading on. With it off, `Pulse.LoadScene` only sets the `_scene` property and completes immediately, and nobody loads the scene automatically. That mode suits games that manage scenes themselves and just want a shared "current scene" value.

## Late joiners

When a player enters a room, the client reads `_scene`. If the room is already in another scene, the client loads it. If the active scene already matches, nothing happens and `OnNetworkSceneLoaded` is not raised for that player, so do not rely on the callback for setup that every player needs. Objects that were spawned in the scene arrive from the server's cache as usual.

The match is by scene name or by build index, whichever form the master used.

## What happens to objects

When a load begins, on every client:

- Network objects that are not scene objects and do not have `Persist Across Scenes` are destroyed locally. The server forgets them and drops their cached events.
- Objects with `Persist Across Scenes` (root objects, marked `DontDestroyOnLoad`) survive and keep their owner.
- Scene objects of the old scene disappear with the scene. After the load, the new scene's objects register, and the master re-registers them with the server.

## Event queueing

From the moment a load starts until it ends, room events are held in a queue: RPCs, state updates, typed messages, `RaiseEvent` events, spawns and destroys. When the scene is ready, the scene objects are registered, the queue is replayed in the original order, and then `OnNetworkSceneLoaded(sceneName)` is raised. The effect is that no RPC or spawn hits an object that does not exist yet.

Property changes, player joins and leaves, and ownership and master events are not queued. The queue holds up to 4096 events. Beyond that the newest events are dropped and a warning is logged. If you leave the room or disconnect during a load, the queue is discarded.

## Callbacks

`Pulse.Events.OnNetworkSceneLoaded` passes the loaded scene's name. A `NetPulseCallbacks` subclass can override `OnNetworkSceneLoaded`, and a class that implements `ISceneCallbacks` can register with `Pulse.AddCallbackTarget`.

The Basic sample shows the flow with two scenes and a pair of master-only buttons that load them. Its player prefab has `Persist Across Scenes` on, so the players stay when the scene changes.
