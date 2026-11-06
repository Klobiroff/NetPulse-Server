# Concepts

This page explains the vocabulary used by every other page: what the server keeps, who may change what, and which NetPulse feature fits which job. Read it once before the how-to pages.

## The big picture

A game client connects to one NetPulse server over reliable UDP. The client identifies itself with an `AppId` and an `AppVersion`, and the server accepts it or rejects it. Once connected, the client can sit in the lobby, create or join a room, and play. Everything that happens during play is scoped to one room.

The server relays messages and enforces a few rules (ownership, who may change the scene, who may set master variables). It does not run your Unity code. Game logic lives in the clients, and optional server plugins can add validation or authority. See the [server overview](index.md) and [getting started](getting-started.md).

On the client, everything runs on Unity's main thread. There are no background callbacks, so you can touch Unity objects from any NetPulse event or `await` continuation.

## Connection states

`Pulse.State` moves through these values:

| State | Meaning |
|---|---|
| `Disconnected` | No connection. The starting and ending state. |
| `Connecting` | The UDP handshake is in progress. |
| `Authenticating` | Connected to the server, waiting for the `AppId` / token check. |
| `Connected` | Ready to join the lobby or a room. |
| `InLobby` | Receiving the live room list. |
| `JoiningRoom` | A create or join request is in flight. |
| `InRoom` | In a room. Objects, RPCs and messages work only here. |
| `Leaving` | A leave request is in flight. |

`NetState` also declares `Disconnecting`, but the current client never enters it. Details are on the [connecting](unity/connecting.md) page.

## Lobby and rooms

A room is the unit of play. It has a name, a maximum player count (`0` means unlimited), an open flag (can others join) and a visible flag (does it appear in the room list), plus custom properties that every member can read. When the last player leaves, the room closes unless the room options ask the server to keep it for a while.

The lobby is an optional directory of visible rooms. Joining the lobby gives you a room list that the server keeps up to date. You do not need the lobby to join a room by name or to join a random room. See [matchmaking](unity/matchmaking.md).

## Players, ActorId and UserId

Every player in a room has three identifiers:

- `ActorId` is a number the server assigns when the player enters the room. It starts at 1 in each new room and counts up. A number is never handed out twice in the same room. Use it to refer to a player inside a room.
- `UserId` identifies the person across sessions. The client generates one and stores it in `PlayerPrefs`, unless `Persist User Id` is off. An authentication plugin can replace it. A player who disconnects and comes back with the same `UserId` can reclaim the old slot, with the same `ActorId`, if the room keeps inactive players.
- `NickName` is a display name. It carries no uniqueness guarantee.

The local player is `Pulse.LocalPlayer`. A player can be inactive: they left with `LeaveRoom(true)` or lost the connection, and the room keeps their slot for a limited time. See [matchmaking](unity/matchmaking.md#leaving-and-rejoining).

## The master client

One player in each room is the master client. The server picks the active player who has been in the room longest, and picks again when that player leaves. Every client is told when it changes.

The master client is an ordinary client with extra duties built into NetPulse:

- Only the master can load a network scene ([scenes](unity/scenes.md)).
- Only the master can change `Master` sync variables ([state sync](unity/state-sync.md)).
- Scene objects belong to the master ([network objects](unity/network-objects.md)).
- `RpcTarget.MasterClient` delivers to it.

Because it is a client, treat the master as a convenience, not as a trusted authority. The server checks ownership and the master-only rules, and plugins can check more.

## Events, receivers and caching

Underneath, everything a client sends into a room is an event: an RPC, a typed message, a spawn, a state update. An event has a code, a payload, a set of receivers, a delivery mode, an optional interest group and an optional caching mode.

- Receivers: everyone else (`Others`), everyone including the sender (`All`), the master client only, or a list of specific actors.
- Delivery: `ReliableOrdered`, `ReliableUnordered`, `UnreliableSequenced` or `Unreliable`. Reliable modes retransmit, unreliable ones do not.
- Caching: an event can be stored in the room's cache. The server replays the cache to every player who joins later, which is how late joiners see objects that were spawned before they arrived. Buffered RPCs and spawns use it.

Codes 0 to 199 are free for your own events. Codes 230 to 250 are reserved for the system. Most code never needs to touch event codes: [RPCs](unity/rpc.md) and [messages](unity/messages.md) wrap them. The [low-level API](unity/low-level-api.md) page covers `Pulse.RaiseEvent`.

## Network objects and ViewId

A network object is a GameObject with a `NetworkObject` component. Each one has a `ViewId`, an integer that is the same on every client and names the object in the room.

- Objects you create with `Pulse.Instantiate` get a view id of `ActorId * 1000 + n`. So the actor with id 2 creates objects numbered 2001, 2002 and so on, and each player can have up to 999 live objects at once.
- Scene objects (placed in the scene in the editor) use their `Scene Id`, 1 to 999, as the view id.

Every network object has one owner. The owner is the only client that sends the object's state, and the only one whose `IsMine` is true. Other clients receive the state and apply it. Every `NetworkBehaviour` on the object (the root and its children) is addressed by its index, so the hierarchy must match on all clients. See [network objects](unity/network-objects.md).

## Ownership modes

Each object has an ownership mode, set on the `NetworkObject` and sent to the server when the object spawns:

| Mode | Who can become the owner |
|---|---|
| `Fixed` | Nobody. The creator keeps the object. |
| `Takeover` | Anyone, immediately, by requesting it. |
| `Request` | Anyone, but the current owner has to approve. |

The server validates every ownership change and every state update, so a client cannot write to an object it does not own. When a player leaves, the objects they own at that moment are destroyed, except scene objects, which return to the master. See [ownership](unity/ownership.md).

## State, RPCs, messages and properties

NetPulse gives you several ways to move data. Pick by the shape of the data:

| You want to | Use |
|---|---|
| Store room-wide data that late joiners also get (map, round number, score) | Room properties, usually typed with `RoomKey<T>` ([rooms and properties](unity/rooms-and-properties.md)) |
| Store per-player data that others can read (team, ready flag) | Player properties, with `PlayerKey<T>` |
| Tell the room something that is not tied to an object (chat, a match event) | A typed message ([messages](unity/messages.md)) |
| Make something happen on a specific object on other clients (fire, hit, open a door) | An RPC ([RPC](unity/rpc.md)) |
| Keep one value on an object in sync (health, color, score) | A `SyncVar<T>` ([state sync](unity/state-sync.md)) |
| Stream a custom, frequently changing blob for an object | `[Synced]` fields or `OnSerialize` |
| Move a transform, rigidbody or animator | The ready-made components ([components](unity/components.md)) |

A rule of thumb: properties are data the room remembers, RPCs and messages are things that happen once, and `SyncVar` and state sync are values that stay equal across clients.

## Server time and ticks

The server has a clock. The client estimates the offset to it from the handshake and from a ping every two seconds, and exposes the result as `Pulse.ServerTime` in seconds. All clients see nearly the same value, which makes it the right basis for interpolation and for timing things that must line up.

The server also announces a tick rate (30 by default). A tick is a numbered slice of server time: `Pulse.Tick` is `floor(ServerTime * TickRate)`. `NetworkBehaviour.OnNetworkTick` runs once per tick, and state is sent on tick boundaries. See [ticks and time](unity/ticks-and-time.md).

## Interest groups

An interest group limits who receives an event. Group 0 means everyone. An event sent to group 5 reaches only the players who subscribed to group 5 with `Pulse.ChangeInterestGroups`. You can set a group on a network object so that all its traffic is filtered, and use it for areas, teams or levels of detail. See [low-level API](unity/low-level-api.md#interest-groups).

Two limits apply. Events that go to the master client or to a list of specific actors ignore the group. And the room cache is replayed once, when a player joins, so subscribing to a group later does not deliver the cached events of that group retroactively.
