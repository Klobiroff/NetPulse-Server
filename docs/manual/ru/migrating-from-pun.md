# Переход с PUN

Если вы знаете Photon PUN 2, большая часть NetPulse покажется знакомой: комнаты, мастер-клиент, `NetworkObject` в роли `PhotonView`, `[NetRpc]` в роли `[PunRPC]`, режимы владения, пользовательские свойства. На этой странице сопоставлены понятия и API, которые в NetPulse действительно есть, а затем перечислены отличия.

## Подключение и комнаты

| PUN 2 | NetPulse |
|---|---|
| `PhotonNetwork.ConnectUsingSettings()` | `Pulse.Connect()`; настройки лежат в `NetPulseSettings` |
| `PhotonServerSettings` (AppId, регион) | `NetPulseSettings` (хост, порт, `AppId`, `AppVersion`) |
| `PhotonNetwork.GameVersion` | `AppVersion` в настройках |
| `PhotonNetwork.NickName` | `Pulse.NickName` |
| `PhotonNetwork.AuthValues` | `Pulse.AuthToken`, проверяется серверным плагином аутентификации |
| `PhotonNetwork.NetworkClientState` | `Pulse.State` (`NetState`) |
| `PhotonNetwork.IsConnected`, `InRoom` | `Pulse.IsConnected`, `Pulse.IsInRoom` |
| `PhotonNetwork.GetPing()` | `Pulse.RoundTripTimeMs` |
| `PhotonNetwork.Disconnect()` | `Pulse.Disconnect()` |
| `PhotonNetwork.OfflineMode` | `Pulse.OfflineMode` |
| `PhotonNetwork.JoinLobby()`, `LeaveLobby()` | `Pulse.JoinLobby()`, `Pulse.LeaveLobby()` |
| `OnRoomListUpdate(List<RoomInfo>)` (только изменения) | `OnRoomListUpdate(IReadOnlyList<RoomInfo>)`, всегда полный объединённый список; также `Pulse.LobbyRooms`, `Pulse.GetRoomList()` |
| `PhotonNetwork.CreateRoom(name, options)` | `Pulse.CreateRoom(name, options)` |
| `PhotonNetwork.JoinRoom(name)` | `Pulse.JoinRoom(name)` |
| `PhotonNetwork.JoinOrCreateRoom(name, options, lobby)` | `Pulse.JoinOrCreateRoom(name, options)` |
| `PhotonNetwork.JoinRandomRoom(props, maxPlayers)` | `Pulse.JoinRandomRoom(expectedProperties, expectedMaxPlayers, createOptions)`; может создать комнату, если ничего не подошло |
| `PhotonNetwork.LeaveRoom(becomeInactive)` | `Pulse.LeaveRoom(becomeInactive)` |
| `RoomOptions.MaxPlayers`, `IsVisible`, `IsOpen` | Те же имена в `RoomOptions` |
| `RoomOptions.CustomRoomProperties` | `RoomOptions.CustomProperties` |
| `RoomOptions.CustomRoomPropertiesForLobby` | `RoomOptions.LobbyProperties` |
| `RoomOptions.PlayerTtl`, `EmptyRoomTtl` | `RoomOptions.PlayerTtlMs`, `EmptyRoomTtlMs` |
| `PhotonNetwork.CurrentRoom`, `LocalPlayer`, `IsMasterClient` | `Pulse.CurrentRoom`, `Pulse.LocalPlayer`, `Pulse.IsMasterClient` |
| `PhotonNetwork.PlayerList` | `Pulse.CurrentRoom.Players` |
| `PhotonNetwork.MasterClient` | `Pulse.CurrentRoom.MasterClient` |
| `PhotonNetwork.Time` | `Pulse.ServerTime`, в секундах типа `double` |
| `PhotonNetwork.SendRate`, `SerializationRate` | Поля с теми же именами в `NetPulseSettings` |
| `PhotonNetwork.LoadLevel(scene)` с `AutomaticallySyncScene` | `Pulse.LoadScene(scene)` с `Auto Sync Scene`, только мастер |

## Игроки и свойства

| PUN 2 | NetPulse |
|---|---|
| `Player.ActorNumber` | `Player.ActorId` |
| `Player.NickName`, `UserId`, `IsLocal`, `IsMasterClient`, `IsInactive` | Те же имена |
| Пользовательские свойства `Hashtable` | `PropertyTable`; `player.SetProperty(key, value)`, `GetProperty<T>(key)` |
| `SetCustomProperties(props, expectedProps)` | `SetProperties(delta, expected)` (compare-and-set); типизированные `RoomKey<T>` и `PlayerKey<T>` с `Set`, `Get`, `SetAsync` |
| `OnRoomPropertiesUpdate(Hashtable)` | `OnRoomPropertiesChanged(PropertyTable)`, `room.OnChanged(key, handler)` |
| `OnPlayerPropertiesUpdate(Player, Hashtable)` | `OnPlayerPropertiesChanged(Player, PropertyTable)`, `player.OnChanged(key, handler)` |
| `Room.IsOpen = false` | `room.SetProperty("_isOpen", false)`, только мастер |

## Объекты, RPC и состояние

| PUN 2 | NetPulse |
|---|---|
| `PhotonView` | `NetworkObject` |
| `MonoBehaviourPun`, `photonView.IsMine` | `NetworkBehaviour`, `IsMine` |
| `photonView.ViewID`, `PhotonView.Find(id)` | `NetworkObject.ViewId`, `Pulse.FindObject(viewId)` |
| `photonView.Owner`, `OwnerActorNr` | `Owner`, `OwnerActorId` |
| `PhotonNetwork.Instantiate("PrefabName", ...)` из Resources | `Pulse.Instantiate(prefab, ...)` с `NetworkPrefabRegistry`; перегрузка по имени тоже есть |
| `photonView.InstantiationData` (`object[]`) | `NetworkObject.InstantiationData` (один `object`) |
| `IPunInstantiateMagicCallback.OnPhotonInstantiate` | `OnNetworkSpawned()` |
| `PhotonNetwork.Destroy(go)` | `Pulse.Destroy(go)` |
| `[PunRPC]` и `photonView.RPC("Name", RpcTarget.All, args)` | `[NetRpc]` и `Rpc.Name(args)` или `Rpc.To(RpcTarget.All).Name(args)`; строковая форма: `CallRpc(nameof(Name), RpcTarget.All, args)` |
| `RpcTarget.All`, `Others`, `MasterClient`, `AllBuffered`, `OthersBuffered` | Те же имена. `RpcTarget.Owner` новый. Вариантов `...ViaServer` нет. |
| `PhotonMessageInfo` | `NetRpcInfo` (`Sender`, `SenderActorId`, `ServerTime`) |
| `IPunObservable.OnPhotonSerializeView(stream, info)` | `OnSerialize(NetStream stream)` |
| `stream.IsWriting`, `SendNext`, `ReceiveNext()` | То же, с `ReceiveNext<T>()`; либо `stream.Serialize(ref value)` для обоих направлений |
| Добавление скрипта в `Observed Components` | Не нужно. Синхронизируется любой `NetworkBehaviour`, который переопределяет `OnSerialize` или содержит `SyncVar`. |
| `PhotonTransformView`, `PhotonRigidbodyView`, `PhotonAnimatorView` | `NetworkTransform`, `NetworkRigidbody`, `NetworkAnimator` |
| `OwnershipOption.Fixed`, `Takeover`, `Request` | `OwnershipMode.Fixed`, `Takeover`, `Request` |
| `photonView.RequestOwnership()`, `TransferOwnership(player)` | `networkObject.RequestOwnership()`, `TransferOwnership(player)`, плюс ожидаемые версии `...Async` |
| `OnOwnershipRequest(PhotonView, Player)` и одобрение через `TransferOwnership(requester)` | `OnOwnershipRequest(Player)` и `networkObject.ApproveOwnership(requester)` |
| `PhotonNetwork.SetInterestGroups`, `PhotonView.Group` | `Pulse.ChangeInterestGroups`, `NetworkObject.InterestGroup` |

В PUN нет синхронизации отдельных значений вроде `SyncVar<T>`. Если вы дублировали одно значение объекта через пользовательские свойства или RPC, его может заменить `SyncVar<T>` ([синхронизация состояния](unity/state-sync.md)).

## События и колбэки

| PUN 2 | NetPulse |
|---|---|
| `MonoBehaviourPunCallbacks` | `NetPulseCallbacks` |
| `PhotonNetwork.AddCallbackTarget(obj)` | `Pulse.AddCallbackTarget(obj)` |
| `IConnectionCallbacks` | `INetPulseConnectionCallbacks` (`OnConnected`, `OnDisconnected(string cause)`) |
| `IMatchmakingCallbacks` | `IMatchmakingCallbacks` |
| `IInRoomCallbacks` | `IRoomCallbacks` |
| `ILobbyCallbacks` | `ILobbyCallbacks` |
| `OnConnectedToMaster` | `OnConnected`. Сервер один, перехода между master-сервером и игровым сервером нет. |
| `PhotonNetwork.RaiseEvent(code, content, options, sendOptions)` | `Pulse.RaiseEvent(code, data, new RaiseEventOptions { ... })`, коды от 0 до 199 |
| `ReceiverGroup`, `EventCaching`, `TargetActors` | `ReceiverGroup` (плюс `Targets`), `CachingOption`, `TargetActors` |
| `IOnEventCallback.OnEvent(EventData)` | `IOnEventCallback.OnEvent(code, senderActorId, data)` |
| Аналога нет | События C# в `Pulse.Events`, например `Pulse.Events.OnJoinedRoom`, как альтернатива интерфейсам колбэков |

У нового типизированного слоя аналога в PUN нет: `Pulse.Send<T>` и `Pulse.On<T>` для типизированных сообщений комнаты ([сообщения](unity/messages.md)).

## Главные отличия

- Самостоятельный хостинг. Вы запускаете сервер NetPulse и подключаетесь к его адресу. Регионов, сервера имён и облачного app id нет. Серверную логику добавляют плагинами ([быстрый старт](getting-started.md)).
- Одна точка входа, `Pulse`, вместо `PhotonNetwork`. (Имя `NetPulse` занято пространством имён.)
- Операции можно ждать. `Pulse.Connect()`, `JoinRoom` и остальные возвращают `NetOperation`, который работает с `await`, `yield return` и `.Completed`. Колбэки при этом сохраняются ([async](unity/async.md)).
- Генерация исходников. RPC, `SyncVar<T>`, сообщения и типы `[NetSerializable]` генерируются при компиляции, поэтому использующие их классы объявляются `partial` ([анализаторы](unity/analyzers.md)). PUN находит методы `[PunRPC]` через рефлексию.
- Папки Resources для префабов нет. Создание объектов идёт через реестр префабов, который редактор поддерживает в актуальном состоянии ([сетевые объекты](unity/network-objects.md)).
- Владение проверяет сервер. `IsMine` меняется только тогда, когда это объявляет сервер, в том числе для ваших собственных запросов, а сервер отклоняет обновления состояния, уничтожение объектов и операции «только для мастера» от неверного отправителя ([владение](unity/ownership.md)).
- Master-переменные. `SyncVar<T>` с `SyncPermission.Master` даёт мастеру значение, которое больше никто не может записать.
- Тики. Сервер публикует частоту тиков, а `OnNetworkTick` даёт у всех клиентов колбэк с фиксированным шагом ([тики и время](unity/ticks-and-time.md)).
- `NetRpcInfo.ServerTime` содержит серверное время на принимающем клиенте в момент выполнения вызова. `info.SentServerTime` в PUN содержит время отправки. Если время отправки нужно, положите `Pulse.ServerTime` в аргументы.
- `OnNetworkSpawned` ждёт первого состояния чужого объекта, так что вы не видите значений по умолчанию.
- Загрузка сцен встроена и ставит события в очередь на время загрузки ([сцены](unity/scenes.md)).

## Чего нет

Для этих возможностей PUN в NetPulse аналога нет: регионы и сервер имён, `PhotonNetwork.ReconnectAndRejoin` и `RejoinRoom` (возвращайтесь вызовом `JoinRoom` с тем же `UserId`, см. [подбор комнат](unity/matchmaking.md#leaving-and-rejoining)), `PhotonNetwork.CloseConnection` и `SetMasterClient` с клиента (делайте это серверным плагином), получатели RPC `...ViaServer`, списки друзей, WebRPC, внешние провайдеры аутентификации (используйте плагин аутентификации), а также продукты Photon Chat и Voice.
