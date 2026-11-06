# Migrating from PUN

If you know Photon PUN 2, most of NetPulse will look familiar: rooms, a master client, `NetworkObject` in the role of `PhotonView`, `[NetRpc]` in the role of `[PunRPC]`, ownership modes, custom properties. This page maps the concepts and APIs that NetPulse really has, then lists what differs.

## Connection and rooms

| PUN 2 | NetPulse |
|---|---|
| `PhotonNetwork.ConnectUsingSettings()` | `Pulse.Connect()`; settings live in `NetPulseSettings` |
| `PhotonServerSettings` (AppId, region) | `NetPulseSettings` (host, port, `AppId`, `AppVersion`) |
| `PhotonNetwork.GameVersion` | `AppVersion` in the settings |
| `PhotonNetwork.NickName` | `Pulse.NickName` |
| `PhotonNetwork.AuthValues` | `Pulse.AuthToken`, checked by a server auth plugin |
| `PhotonNetwork.NetworkClientState` | `Pulse.State` (`NetState`) |
| `PhotonNetwork.IsConnected`, `InRoom` | `Pulse.IsConnected`, `Pulse.IsInRoom` |
| `PhotonNetwork.GetPing()` | `Pulse.RoundTripTimeMs` |
| `PhotonNetwork.Disconnect()` | `Pulse.Disconnect()` |
| `PhotonNetwork.OfflineMode` | `Pulse.OfflineMode` |
| `PhotonNetwork.JoinLobby()`, `LeaveLobby()` | `Pulse.JoinLobby()`, `Pulse.LeaveLobby()` |
| `OnRoomListUpdate(List<RoomInfo>)` (changes only) | `OnRoomListUpdate(IReadOnlyList<RoomInfo>)`, always the full merged list; also `Pulse.LobbyRooms`, `Pulse.GetRoomList()` |
| `PhotonNetwork.CreateRoom(name, options)` | `Pulse.CreateRoom(name, options)` |
| `PhotonNetwork.JoinRoom(name)` | `Pulse.JoinRoom(name)` |
| `PhotonNetwork.JoinOrCreateRoom(name, options, lobby)` | `Pulse.JoinOrCreateRoom(name, options)` |
| `PhotonNetwork.JoinRandomRoom(props, maxPlayers)` | `Pulse.JoinRandomRoom(expectedProperties, expectedMaxPlayers, createOptions)`; can create a room when none matches |
| `PhotonNetwork.LeaveRoom(becomeInactive)` | `Pulse.LeaveRoom(becomeInactive)` |
| `RoomOptions.MaxPlayers`, `IsVisible`, `IsOpen` | Same names in `RoomOptions` |
| `RoomOptions.CustomRoomProperties` | `RoomOptions.CustomProperties` |
| `RoomOptions.CustomRoomPropertiesForLobby` | `RoomOptions.LobbyProperties` |
| `RoomOptions.PlayerTtl`, `EmptyRoomTtl` | `RoomOptions.PlayerTtlMs`, `EmptyRoomTtlMs` |
| `PhotonNetwork.CurrentRoom`, `LocalPlayer`, `IsMasterClient` | `Pulse.CurrentRoom`, `Pulse.LocalPlayer`, `Pulse.IsMasterClient` |
| `PhotonNetwork.PlayerList` | `Pulse.CurrentRoom.Players` |
| `PhotonNetwork.MasterClient` | `Pulse.CurrentRoom.MasterClient` |
| `PhotonNetwork.Time` | `Pulse.ServerTime`, in seconds as a `double` |
| `PhotonNetwork.SendRate`, `SerializationRate` | Fields of the same name in `NetPulseSettings` |
| `PhotonNetwork.LoadLevel(scene)` with `AutomaticallySyncScene` | `Pulse.LoadScene(scene)` with `Auto Sync Scene`, master only |

## Players and properties

| PUN 2 | NetPulse |
|---|---|
| `Player.ActorNumber` | `Player.ActorId` |
| `Player.NickName`, `UserId`, `IsLocal`, `IsMasterClient`, `IsInactive` | Same names |
| `Hashtable` custom properties | `PropertyTable`; `player.SetProperty(key, value)`, `GetProperty<T>(key)` |
| `SetCustomProperties(props, expectedProps)` | `SetProperties(delta, expected)` (compare-and-set); typed `RoomKey<T>` and `PlayerKey<T>` with `Set`, `Get`, `SetAsync` |
| `OnRoomPropertiesUpdate(Hashtable)` | `OnRoomPropertiesChanged(PropertyTable)`, `room.OnChanged(key, handler)` |
| `OnPlayerPropertiesUpdate(Player, Hashtable)` | `OnPlayerPropertiesChanged(Player, PropertyTable)`, `player.OnChanged(key, handler)` |
| `Room.IsOpen = false` | `room.SetProperty("_isOpen", false)`, master only |

## Objects, RPCs and state

| PUN 2 | NetPulse |
|---|---|
| `PhotonView` | `NetworkObject` |
| `MonoBehaviourPun`, `photonView.IsMine` | `NetworkBehaviour`, `IsMine` |
| `photonView.ViewID`, `PhotonView.Find(id)` | `NetworkObject.ViewId`, `Pulse.FindObject(viewId)` |
| `photonView.Owner`, `OwnerActorNr` | `Owner`, `OwnerActorId` |
| `PhotonNetwork.Instantiate("PrefabName", ...)` from Resources | `Pulse.Instantiate(prefab, ...)` with the `NetworkPrefabRegistry`; a name overload exists |
| `photonView.InstantiationData` (`object[]`) | `NetworkObject.InstantiationData` (one `object`) |
| `IPunInstantiateMagicCallback.OnPhotonInstantiate` | `OnNetworkSpawned()` |
| `PhotonNetwork.Destroy(go)` | `Pulse.Destroy(go)` |
| `[PunRPC]` and `photonView.RPC("Name", RpcTarget.All, args)` | `[NetRpc]` and `Rpc.Name(args)` or `Rpc.To(RpcTarget.All).Name(args)`; the string form is `CallRpc(nameof(Name), RpcTarget.All, args)` |
| `RpcTarget.All`, `Others`, `MasterClient`, `AllBuffered`, `OthersBuffered` | The same names. `RpcTarget.Owner` is new. There are no `...ViaServer` variants. |
| `PhotonMessageInfo` | `NetRpcInfo` (`Sender`, `SenderActorId`, `ServerTime`) |
| `IPunObservable.OnPhotonSerializeView(stream, info)` | `OnSerialize(NetStream stream)` |
| `stream.IsWriting`, `SendNext`, `ReceiveNext()` | The same, with `ReceiveNext<T>()`; or `stream.Serialize(ref value)` for both directions |
| Adding the script to `Observed Components` | Not needed. Any `NetworkBehaviour` that overrides `OnSerialize` or has `SyncVar`s is synchronized. |
| `PhotonTransformView`, `PhotonRigidbodyView`, `PhotonAnimatorView` | `NetworkTransform`, `NetworkRigidbody`, `NetworkAnimator` |
| `OwnershipOption.Fixed`, `Takeover`, `Request` | `OwnershipMode.Fixed`, `Takeover`, `Request` |
| `photonView.RequestOwnership()`, `TransferOwnership(player)` | `networkObject.RequestOwnership()`, `TransferOwnership(player)`, plus awaitable `...Async` versions |
| `OnOwnershipRequest(PhotonView, Player)` and approving with `TransferOwnership(requester)` | `OnOwnershipRequest(Player)` and `networkObject.ApproveOwnership(requester)` |
| `PhotonNetwork.SetInterestGroups`, `PhotonView.Group` | `Pulse.ChangeInterestGroups`, `NetworkObject.InterestGroup` |

PUN has no per-value sync like `SyncVar<T>`. If you used custom properties or RPCs to mirror one value of an object, `SyncVar<T>` can replace them ([state sync](unity/state-sync.md)).

## Events and callbacks

| PUN 2 | NetPulse |
|---|---|
| `MonoBehaviourPunCallbacks` | `NetPulseCallbacks` |
| `PhotonNetwork.AddCallbackTarget(obj)` | `Pulse.AddCallbackTarget(obj)` |
| `IConnectionCallbacks` | `INetPulseConnectionCallbacks` (`OnConnected`, `OnDisconnected(string cause)`) |
| `IMatchmakingCallbacks` | `IMatchmakingCallbacks` |
| `IInRoomCallbacks` | `IRoomCallbacks` |
| `ILobbyCallbacks` | `ILobbyCallbacks` |
| `OnConnectedToMaster` | `OnConnected`. There is one server and no master/game server hop. |
| `PhotonNetwork.RaiseEvent(code, content, options, sendOptions)` | `Pulse.RaiseEvent(code, data, new RaiseEventOptions { ... })`, codes 0 to 199 |
| `ReceiverGroup`, `EventCaching`, `TargetActors` | `ReceiverGroup` (plus `Targets`), `CachingOption`, `TargetActors` |
| `IOnEventCallback.OnEvent(EventData)` | `IOnEventCallback.OnEvent(code, senderActorId, data)` |
| No equivalent | C# events on `Pulse.Events`, for example `Pulse.Events.OnJoinedRoom`, as an alternative to callback interfaces |

The new typed layer has no PUN counterpart: `Pulse.Send<T>` and `Pulse.On<T>` for typed room messages ([messages](unity/messages.md)).

## Main differences

- Self-hosted. You run the NetPulse server and connect to its address. There are no regions, no name server and no cloud app id. Server logic is added with plugins ([getting started](getting-started.md)).
- One entry point, `Pulse`, not `PhotonNetwork`. (The name `NetPulse` is taken by the namespace.)
- Operations are awaitable. `Pulse.Connect()`, `JoinRoom` and the rest return a `NetOperation` that works with `await`, `yield return` and `.Completed`. The callbacks still exist ([async](unity/async.md)).
- Source generation. RPCs, `SyncVar<T>`, messages and `[NetSerializable]` types are generated at compile time, so classes that use them are `partial` ([analyzers](unity/analyzers.md)). PUN finds `[PunRPC]` methods by reflection.
- No Resources folder for prefabs. Spawning goes through the prefab registry, which is kept up to date by the editor ([network objects](unity/network-objects.md)).
- Server-enforced ownership. `IsMine` changes only when the server announces it, including for your own requests, and the server rejects state updates, destroys and master-only operations from the wrong sender ([ownership](unity/ownership.md)).
- Master variables. `SyncVar<T>` with `SyncPermission.Master` gives the master a value that nobody else may write.
- Ticks. The server publishes a tick rate, and `OnNetworkTick` gives a fixed-step callback on every client ([ticks and time](unity/ticks-and-time.md)).
- `NetRpcInfo.ServerTime` is the server time on the receiving client when the call ran. PUN's `info.SentServerTime` is the send time. Put `Pulse.ServerTime` into the arguments if you need the send time.
- `OnNetworkSpawned` waits for the first state of a remote object, so you do not see default values.
- Scene loading is built in and queues events during the load ([scenes](unity/scenes.md)).

## Not available

NetPulse has no equivalent for these PUN features: regions and name servers, `PhotonNetwork.ReconnectAndRejoin` and `RejoinRoom` (rejoin by calling `JoinRoom` with the same `UserId`, see [matchmaking](unity/matchmaking.md#leaving-and-rejoining)), `PhotonNetwork.CloseConnection` and `SetMasterClient` from the client (do it with a server plugin), the `...ViaServer` RPC targets, friend lists, WebRPC, custom authentication providers (use an auth plugin) and the Photon Chat and Voice products.
