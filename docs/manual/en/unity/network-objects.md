# Network objects

A network object is a GameObject that exists in every client of a room and has the same identity everywhere. You build one from a `NetworkObject` component plus your own `NetworkBehaviour` scripts. This page covers creating, destroying and finding them. State, RPCs and ownership have their own pages.

## Setting up a prefab

1. Add a `NetworkObject` to the root of the prefab.
2. Add your `NetworkBehaviour` scripts, and components such as `NetworkTransform`, to the root or its children.
3. Save the prefab under `Assets`. NetPulse adds it to the `NetworkPrefabRegistry` automatically.

The `NetworkObject` inspector has these settings:

| Setting | Meaning |
|---|---|
| Ownership Mode | `Fixed`, `Takeover` or `Request` ([ownership](ownership.md)) |
| Interest Group | Events of this object reach only players subscribed to this group; `0` is everyone ([low-level API](low-level-api.md#interest-groups)) |
| Persist Across Scenes | Keep the object when the master loads a new network scene ([scenes](scenes.md)). The object must be a root object. |
| Scene Object / Scene Id | For objects placed in the scene (below) |

`NetworkObject` finds every `NetworkBehaviour` on the object and its children, including inactive ones, once in `Awake`, and numbers them in hierarchy order. The number identifies the behaviour in the network traffic, so the hierarchy has to be the same on every client, and you cannot add a `NetworkBehaviour` to a networked object at runtime. An object can hold up to 256 behaviours.

## The prefab registry

Spawning sends a small id, not a name, so every client needs the same table of prefabs. That table is the `NetworkPrefabRegistry` asset, normally `Assets/Resources/NetworkPrefabRegistry.asset`, referenced by the `Prefab Registry` field of the settings.

The registry is rebuilt for you when a prefab under `Assets` is imported, moved or deleted, and before every build. Ids are assigned once per prefab (by its asset GUID) and never reused, so the table stays stable between builds. Prefabs outside the `Assets` folder, for example inside packages, are not picked up. To rebuild by hand, use `NetPulse > Rebuild Prefab Registry` or the button in Project Settings and in the Hub.

Because the ids come from the registry, client and server builds that talk to each other should be built from the same project state. The error "Received spawn for unknown prefab id" means the other side has a prefab yours lacks.

## Instantiate and destroy

```csharp
using NetPulse;
using UnityEngine;

public sealed class Spawner : MonoBehaviour
{
    [SerializeField] private GameObject _playerPrefab;

    private GameObject _player;

    private async void Start()
    {
        await Pulse.Connect();
        await Pulse.JoinOrCreateRoom("arena");
        _player = Pulse.Instantiate(_playerPrefab, new Vector3(0f, 1f, 0f), Quaternion.identity, Color.red);
    }

    private void OnDestroy()
    {
        if (_player != null)
        {
            Pulse.Destroy(_player);
        }
    }
}
```

`Pulse.Instantiate(prefab, position, rotation, data, interestGroup)` creates the object on your client immediately, makes you its owner and tells everyone else to create it. The server caches the spawn, so players who join later receive it too. It returns the new `GameObject`, or `null` after logging an error if you are not in a room (offline mode counts as a room), the prefab is not in the registry, or the prefab has no `NetworkObject` on its root.

The optional `data` argument travels with the spawn. Every client reads it from `NetworkObject.InstantiationData` as soon as the object exists, which is the way to pass a color, a team or a seed. It can be anything the dynamic serializer handles: numbers, `string`, `Guid`, arrays, a `PropertyTable`, and the Unity types `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int` and `Vector3Int`.

```csharp
using NetPulse;
using UnityEngine;

public sealed class Tinted : NetworkBehaviour
{
    [SerializeField] private Renderer _renderer;

    public override void OnNetworkSpawned()
    {
        if (NetworkObject.InstantiationData is Color color)
        {
            _renderer.material.color = color;
        }
    }
}
```

The `interestGroup` argument applies to the spawn event itself: only players subscribed to that group create the object. The object's later traffic (state, RPCs) uses the `Interest Group` set in its `NetworkObject` inspector, so give both the same value.

There is also an overload `Pulse.Instantiate(string prefabName, ...)` that looks the prefab up by its name in the registry. Names must be unique for it to be reliable.

`Pulse.Destroy(gameObject)`, `Pulse.Destroy(networkObject)` and `networkObject.Destroy()` remove the object on every client and drop its cached events from the room. Only the owner or the master client may do it; anyone else gets a warning and nothing happens.

- A plain `Object.Destroy(gameObject)` on a networked object removes it on your client only. The others keep their copy.
- Scene objects cannot be destroyed over the network. `Pulse.Destroy` on one only destroys your local copy.
- When a player leaves the room, the objects they own at that moment are destroyed on every client. Objects they had already handed to someone else stay. A player who is only inactive keeps their objects.
- When you leave the room or lose the connection, all non-scene network objects are destroyed locally.

## What a NetworkObject tells you

| Member | Meaning |
|---|---|
| `ViewId` | The room-wide id ([concepts](../concepts.md#network-objects-and-viewid)) |
| `IsSpawned` | Registered and live in the room |
| `IsMine`, `Owner`, `OwnerActorId` | Who owns it. `IsMine` is also true when you are not in a room. |
| `OwnershipMode` | The mode set in the inspector |
| `InstantiationData`, `PrefabId` | What the spawn carried |
| `IsSceneObject`, `SceneId` | Scene object info |
| `InterestGroup`, `PersistAcrossScenes` | Inspector settings |
| `Behaviours` | The `NetworkBehaviour`s on it |

Inside a `NetworkBehaviour` you also get `NetworkObject`, `IsMine`, `Owner`, `ViewId` and `IsSpawned` directly. `Pulse.FindObject(viewId)` returns the object with that id, or `null`.

## Scene objects

Objects that sit in the scene at design time can be networked too: a door, a pickup, a shared cube. Tick `Scene Object` on the `NetworkObject`. The inspector assigns the next free `Scene Id` (1 to 999) and uses it as the object's view id. Scene ids have to be unique within a scene; a duplicate is reassigned when you save, with a warning.

Scene objects behave a little differently:

- They are not spawned over the network. Each client has its own copy from the loaded scene, and the copies find each other by view id.
- They belong to the master client. The master tells the server about them, with their ownership modes, after it joins and again after it becomes master. A scene object that someone took over and who then left returns to the master.
- They cannot be destroyed over the network and cannot persist across scenes.
- When the master loads a new network scene, the scene objects of the old scene are gone and the new scene's objects register after the load ([scenes](scenes.md)).

## Persist across scenes

A root network object with `Persist Across Scenes` ticked survives `Pulse.LoadScene`. NetPulse calls `DontDestroyOnLoad` on it and tells the server to keep it. All other non-scene network objects are destroyed locally when a network scene starts loading, and the server forgets them. See [scenes](scenes.md).
