# Codes and keys

Every table on this page comes from `NetPulse.Common` (`Codes.cs`, `Enums.cs` and `RoomPropertyKeys.cs`). How they are used on the wire is described in [Wire protocol](protocol.md).

## Operation codes

| Code | Name | Purpose |
|---|---|---|
| 1 | `Authenticate` | Log in. Must be the first operation. |
| 2 | `JoinLobby` | Subscribe to the room list. |
| 3 | `LeaveLobby` | Unsubscribe from the room list. |
| 10 | `CreateRoom` | Create a room and join it. |
| 11 | `JoinRoom` | Join an existing room by name. |
| 12 | `JoinOrCreateRoom` | Join a room, creating it if it does not exist. |
| 13 | `JoinRandomRoom` | Join a random open room that matches a filter. |
| 14 | `LeaveRoom` | Leave the current room. |
| 20 | `RaiseEvent` | Send an event to other players. |
| 21 | `SetRoomProperties` | Change room properties. |
| 22 | `SetPlayerProperties` | Change player properties. |
| 23 | `ChangeInterestGroups` | Join or leave interest groups. |
| 24 | `TransferOwnership` | Give a network object to another player. |
| 25 | `RequestOwnership` | Ask for ownership of a network object. |
| 26 | `RegisterSceneObjects` | Master only: declare the ownership modes of scene objects. |
| 30 | `GetRoomList` | Get the current room list. |

## Event codes

| Code | Name | Sent by | Meaning |
|---|---|---|---|
| 0 to 199 | your own events | clients, plugins | `EventCode.MaxUserEvent` is 199. |
| 230 | `RoomList` | server | Full room list or a delta. |
| 231 | `PlayerJoined` | server | Another player joined. |
| 232 | `PlayerLeft` | server | A player left or became inactive. |
| 233 | `RoomPropertiesChanged` | server | Room property delta. |
| 234 | `PlayerPropertiesChanged` | server | Player property delta. |
| 235 | `MasterClientChanged` | server | A new master client. |
| 236 | `OwnershipChanged` | server | A network object changed owner. |
| 237 | `OwnershipRequested` | server | Sent to the owner of a `Request`-mode object. |
| 240 | `ObjectInstantiate` | clients | A network object was created. |
| 241 | `ObjectDestroy` | clients | A network object was destroyed. |
| 242 | `Rpc` | clients | A remote procedure call. |
| 243 | `StateSync` | clients | State of an object, from its owner. |
| 244 | `Message` | clients | A typed message. |
| 245 | `SyncVarSet` | clients | A master-owned sync variable changed (master only). |
| 250 | `ErrorInfo` | server | A warning or error notice with `ErrorCode` and `Message`. |

Clients may raise codes 0 to 199 and 240 to 245. Every other code is reserved and fails with `Forbidden`. A plugin can broadcast any code.

## Return codes

| Code | Name | Meaning |
|---|---|---|
| 0 | `Ok` | Success. |
| -1 | `InternalError` | An unexpected server error. |
| -2 | `InvalidRequest` | Malformed request, unknown operation, a second join in progress, or the user is already in that room. |
| -3 | `NotAuthenticated` | Authenticate first. |
| -4 | `NotInRoom` | The operation needs a room. |
| -5 | `AlreadyInRoom` | The peer is already in a room. |
| -6 | `Forbidden` | Not allowed: a reserved event or property, or a failed ownership check. |
| -10 | `RoomNotFound` | No such room, or the room closed while joining. |
| -11 | `RoomFull` | The room has reached `MaxPlayers`. |
| -12 | `RoomClosed` | The room is not open. Also the `ErrorInfo` code sent when a room closes. |
| -13 | `RoomAlreadyExists` | `CreateRoom` with a name that is taken. |
| -14 | `NoMatchFound` | `JoinRandomRoom` found nothing and had no options to create a room. |
| -15 | `PropertyCasFailed` | The expected properties did not match. |
| -16 | `ActorNotFound` | The target player is not in the room. |
| -17 | `ObjectNotFound` | The view id is not registered. |
| -20 | `PluginReportedError` | A plugin refused the action. The message is the plugin's. |
| -21 | `PluginNotFound` | The room asked for a plugin that is not installed. |
| -30 | `VersionMismatch` | Different protocol version. |
| -31 | `InvalidAppId` | The AppId does not match the server's. |
| -32 | `AuthenticationFailed` | The authentication plugin refused the client. |

## Parameter keys

| Key | Name | Used for |
|---|---|---|
| 1 | `AppId` | Authenticate: string |
| 2 | `ProtocolVersion` | Authenticate: int |
| 3 | `AppVersion` | Authenticate: string |
| 4 | `UserId` | Authenticate and responses: string |
| 5 | `NickName` | Authenticate and responses: string |
| 6 | `AuthToken` | Authenticate: string |
| 7 | `ServerTimeTicks` | Server time in milliseconds: long |
| 8 | `AuthData` | Authenticate: PropertyTable |
| 10 | `RoomName` | Room operations and room info: string |
| 11 | `Options` | Room options block |
| 12 | `PlayerProperties` | Initial player properties: PropertyTable |
| 13 | `ActorId` | Actor id: int |
| 14 | `MasterClientId` | Actor id of the master: int |
| 15 | `Players` | Player blocks: object[] |
| 16 | `RoomProperties` | Room properties in a join response |
| 17 | `Properties` | Property delta or table |
| 18 | `ExpectedProperties` | Compare-and-set condition, random-join filter |
| 19 | `ExpectedMaxPlayers` | Random-join filter: byte |
| 20 | `LobbyName` | JoinLobby: string |
| 21 | `BecomeInactive` | LeaveRoom: bool |
| 22 | `Broadcast` | Property operations: bool |
| 23 | `IsRejoin` | Join response: bool |
| 24 | `IsOpen` | Room state: bool |
| 25 | `IsVisible` | Room state: bool |
| 26 | `MaxPlayers` | Room state: byte |
| 27 | `Plugin` | Room option: string |
| 28 | `PlayerCount` | Room info: byte |
| 29 | `LobbyProperties` | Room option: string[] |
| 30 | `EventCode` | RaiseEvent: byte |
| 31 | `Data` | Event payload |
| 32 | `Receivers` | RaiseEvent: byte |
| 33 | `TargetActors` | RaiseEvent: int[] |
| 34 | `InterestGroup` | RaiseEvent: byte |
| 35 | `Caching` | RaiseEvent: byte |
| 36 | `Delivery` | RaiseEvent: byte |
| 37 | `Sender` | Event payload: actor id |
| 38 | `ViewId` | Network object id: int |
| 39 | `NewOwnerActorId` | Ownership: int |
| 40 | `GroupsToAdd` | ChangeInterestGroups: byte[] |
| 41 | `GroupsToRemove` | ChangeInterestGroups: byte[] |
| 42 | `PlayerTtlMs` | Room option: int |
| 43 | `EmptyRoomTtlMs` | Room option: int |
| 44 | `OwnershipMode` | RaiseEvent for an instantiate: byte |
| 45 | `ViewIds` | RegisterSceneObjects: int[] |
| 46 | `OwnershipModes` | RegisterSceneObjects: byte[] |
| 47 | `RequesterActorId` | OwnershipRequested: int |
| 48 | `ObjectFlags` | RaiseEvent for an instantiate: byte |
| 49 | `TickRate` | Join response: int |
| 50 | `IsFullList` | RoomList: bool |
| 51 | `Rooms` | RoomList: object[] |
| 52 | `RemovedRooms` | RoomList: string[] |
| 53 | `IsInactive` | Player info: bool |
| 54 | `ErrorCode` | ErrorInfo: int |
| 55 | `Message` | ErrorInfo: string |

## Value type codes

| Code | Name |
|---|---|
| 0 | `Null` |
| 1 | `Bool` |
| 2 | `Byte` |
| 3 | `Short` |
| 4 | `Int` |
| 5 | `Long` |
| 6 | `Float` |
| 7 | `Double` |
| 8 | `String` |
| 9 | `ByteArray` |
| 10 | `IntArray` |
| 11 | `FloatArray` |
| 12 | `StringArray` |
| 13 | `ObjectArray` |
| 14 | `Dictionary` |
| 15 | `PropertyTable` |
| 16 | `Guid` |
| 17 | `ParameterDictionary` |
| 18 | `Extended` |
| 100 to 199 | Custom types (`CustomMin`, `CustomMax`) |

The Unity package uses 100 to 105 for `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int` and `Vector3Int`. Payload layouts are in [Wire protocol](protocol.md#value-types).

## Enumerations

| Enum | Values |
|---|---|
| `MessageKind` | 1 `OperationRequest`, 2 `OperationResponse`, 3 `Event`, 4 `Ping`, 5 `Pong` |
| `DeliveryMode` | 0 `ReliableOrdered`, 1 `ReliableUnordered`, 2 `UnreliableSequenced`, 3 `Unreliable` |
| `ReceiverGroup` | 0 `Others`, 1 `All`, 2 `MasterClient`, 3 `Targets` |
| `CachingOption` | 0 `DoNotCache`, 1 `AddToRoomCache`, 2 `AddToRoomCacheGlobal`, 3 `RemoveFromRoomCache`, 4 `RemoveFromCacheForActorsLeft` |
| `ObjectOwnershipMode` | 0 `Fixed`, 1 `Takeover`, 2 `Request` |
| `ObjectFlag` | 1 `PersistAcrossScenes` (a bit flag in `ObjectFlags`) |

## Reserved room property keys

Only the master client may set these keys. See [Security](../server/security.md#reserved-properties).

| Key | Constant | Type | Meaning |
|---|---|---|---|
| `_isOpen` | `RoomPropertyKeys.IsOpen` | bool | Whether new players can join. |
| `_isVisible` | `RoomPropertyKeys.IsVisible` | bool | Whether the room is in the lobby list. |
| `_maxPlayers` | `RoomPropertyKeys.MaxPlayers` | int, 0 to 255 | The player limit. |
| `_scene` | `RoomPropertyKeys.Scene` | string | The current network scene. A change removes scene-bound objects. |

## View ids

| Range | Meaning |
|---|---|
| 1 to 999 | Scene objects. Owned by the master client unless assigned. |
| 1000 and up | Objects created by players. `viewId / 1000` is the creator's actor id, so actor 3 owns 3000 to 3999. |
