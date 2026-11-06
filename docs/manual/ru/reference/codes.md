# Коды и ключи

Все таблицы на этой странице взяты из `NetPulse.Common` (`Codes.cs`, `Enums.cs` и `RoomPropertyKeys.cs`). Как они используются в сети, описано в разделе [Сетевой протокол](protocol.md).

## Коды операций

| Код | Название | Назначение |
|---|---|---|
| 1 | `Authenticate` | Вход в систему. Должна быть первой операцией. |
| 2 | `JoinLobby` | Подписка на список комнат. |
| 3 | `LeaveLobby` | Отписка от списка комнат. |
| 10 | `CreateRoom` | Создать комнату и войти в неё. |
| 11 | `JoinRoom` | Войти в существующую комнату по имени. |
| 12 | `JoinOrCreateRoom` | Войти в комнату, а если её нет, создать. |
| 13 | `JoinRandomRoom` | Войти в случайную открытую комнату, подходящую под фильтр. |
| 14 | `LeaveRoom` | Выйти из текущей комнаты. |
| 20 | `RaiseEvent` | Отправить событие другим игрокам. |
| 21 | `SetRoomProperties` | Изменить свойства комнаты. |
| 22 | `SetPlayerProperties` | Изменить свойства игрока. |
| 23 | `ChangeInterestGroups` | Войти в группы интереса или выйти из них. |
| 24 | `TransferOwnership` | Передать сетевой объект другому игроку. |
| 25 | `RequestOwnership` | Запросить владение сетевым объектом. |
| 26 | `RegisterSceneObjects` | Только мастер: объявить режимы владения сценовых объектов. |
| 30 | `GetRoomList` | Получить текущий список комнат. |

## Коды событий

| Код | Название | Кто отправляет | Значение |
|---|---|---|---|
| от 0 до 199 | ваши события | клиенты, плагины | `EventCode.MaxUserEvent` равен 199. |
| 230 | `RoomList` | сервер | Полный список комнат или дельта. |
| 231 | `PlayerJoined` | сервер | Вошёл другой игрок. |
| 232 | `PlayerLeft` | сервер | Игрок вышел или стал неактивным. |
| 233 | `RoomPropertiesChanged` | сервер | Дельта свойств комнаты. |
| 234 | `PlayerPropertiesChanged` | сервер | Дельта свойств игрока. |
| 235 | `MasterClientChanged` | сервер | Новый мастер-клиент. |
| 236 | `OwnershipChanged` | сервер | У сетевого объекта сменился владелец. |
| 237 | `OwnershipRequested` | сервер | Отправляется владельцу объекта в режиме `Request`. |
| 240 | `ObjectInstantiate` | клиенты | Создан сетевой объект. |
| 241 | `ObjectDestroy` | клиенты | Сетевой объект уничтожен. |
| 242 | `Rpc` | клиенты | Удалённый вызов процедуры. |
| 243 | `StateSync` | клиенты | Состояние объекта от его владельца. |
| 244 | `Message` | клиенты | Типизированное сообщение. |
| 245 | `SyncVarSet` | клиенты | Изменилась sync-переменная мастера (только мастер). |
| 250 | `ErrorInfo` | сервер | Уведомление о предупреждении или ошибке с `ErrorCode` и `Message`. |

Клиенты могут отправлять коды от 0 до 199 и от 240 до 245. Все остальные коды зарезервированы и завершаются `Forbidden`. Плагин может разослать любой код.

## Коды возврата

| Код | Название | Значение |
|---|---|---|
| 0 | `Ok` | Успех. |
| -1 | `InternalError` | Непредвиденная ошибка сервера. |
| -2 | `InvalidRequest` | Некорректный запрос, неизвестная операция, второй вход в процессе или пользователь уже в этой комнате. |
| -3 | `NotAuthenticated` | Сначала пройдите аутентификацию. |
| -4 | `NotInRoom` | Операции нужна комната. |
| -5 | `AlreadyInRoom` | Пир уже находится в комнате. |
| -6 | `Forbidden` | Не разрешено: зарезервированное событие или свойство либо не пройдена проверка владения. |
| -10 | `RoomNotFound` | Такой комнаты нет или она закрылась во время входа. |
| -11 | `RoomFull` | В комнате достигнут `MaxPlayers`. |
| -12 | `RoomClosed` | Комната не открыта. Также код `ErrorInfo`, который отправляется при закрытии комнаты. |
| -13 | `RoomAlreadyExists` | `CreateRoom` с занятым именем. |
| -14 | `NoMatchFound` | `JoinRandomRoom` ничего не нашёл, а параметров для создания комнаты не было. |
| -15 | `PropertyCasFailed` | Ожидаемые свойства не совпали. |
| -16 | `ActorNotFound` | Целевого игрока нет в комнате. |
| -17 | `ObjectNotFound` | View id не зарегистрирован. |
| -20 | `PluginReportedError` | Плагин отклонил действие. Сообщение принадлежит плагину. |
| -21 | `PluginNotFound` | Комната запросила плагин, который не установлен. |
| -30 | `VersionMismatch` | Другая версия протокола. |
| -31 | `InvalidAppId` | AppId не совпадает с серверным. |
| -32 | `AuthenticationFailed` | Плагин аутентификации отказал клиенту. |

## Ключи параметров {#parameter-keys}

| Ключ | Название | Для чего используется |
|---|---|---|
| 1 | `AppId` | Authenticate: строка |
| 2 | `ProtocolVersion` | Authenticate: int |
| 3 | `AppVersion` | Authenticate: строка |
| 4 | `UserId` | Authenticate и ответы: строка |
| 5 | `NickName` | Authenticate и ответы: строка |
| 6 | `AuthToken` | Authenticate: строка |
| 7 | `ServerTimeTicks` | Серверное время в миллисекундах: long |
| 8 | `AuthData` | Authenticate: PropertyTable |
| 10 | `RoomName` | Операции с комнатами и информация о комнате: строка |
| 11 | `Options` | Блок настроек комнаты |
| 12 | `PlayerProperties` | Начальные свойства игрока: PropertyTable |
| 13 | `ActorId` | Идентификатор актёра: int |
| 14 | `MasterClientId` | Идентификатор актёра мастера: int |
| 15 | `Players` | Блоки игроков: object[] |
| 16 | `RoomProperties` | Свойства комнаты в ответе на вход |
| 17 | `Properties` | Дельта свойств или таблица |
| 18 | `ExpectedProperties` | Условие CAS, фильтр случайного входа |
| 19 | `ExpectedMaxPlayers` | Фильтр случайного входа: byte |
| 20 | `LobbyName` | JoinLobby: строка |
| 21 | `BecomeInactive` | LeaveRoom: bool |
| 22 | `Broadcast` | Операции со свойствами: bool |
| 23 | `IsRejoin` | Ответ на вход: bool |
| 24 | `IsOpen` | Состояние комнаты: bool |
| 25 | `IsVisible` | Состояние комнаты: bool |
| 26 | `MaxPlayers` | Состояние комнаты: byte |
| 27 | `Plugin` | Настройка комнаты: строка |
| 28 | `PlayerCount` | Информация о комнате: byte |
| 29 | `LobbyProperties` | Настройка комнаты: string[] |
| 30 | `EventCode` | RaiseEvent: byte |
| 31 | `Data` | Нагрузка события |
| 32 | `Receivers` | RaiseEvent: byte |
| 33 | `TargetActors` | RaiseEvent: int[] |
| 34 | `InterestGroup` | RaiseEvent: byte |
| 35 | `Caching` | RaiseEvent: byte |
| 36 | `Delivery` | RaiseEvent: byte |
| 37 | `Sender` | Нагрузка события: идентификатор актёра |
| 38 | `ViewId` | Идентификатор сетевого объекта: int |
| 39 | `NewOwnerActorId` | Владение: int |
| 40 | `GroupsToAdd` | ChangeInterestGroups: byte[] |
| 41 | `GroupsToRemove` | ChangeInterestGroups: byte[] |
| 42 | `PlayerTtlMs` | Настройка комнаты: int |
| 43 | `EmptyRoomTtlMs` | Настройка комнаты: int |
| 44 | `OwnershipMode` | RaiseEvent при создании объекта: byte |
| 45 | `ViewIds` | RegisterSceneObjects: int[] |
| 46 | `OwnershipModes` | RegisterSceneObjects: byte[] |
| 47 | `RequesterActorId` | OwnershipRequested: int |
| 48 | `ObjectFlags` | RaiseEvent при создании объекта: byte |
| 49 | `TickRate` | Ответ на вход: int |
| 50 | `IsFullList` | RoomList: bool |
| 51 | `Rooms` | RoomList: object[] |
| 52 | `RemovedRooms` | RoomList: string[] |
| 53 | `IsInactive` | Информация об игроке: bool |
| 54 | `ErrorCode` | ErrorInfo: int |
| 55 | `Message` | ErrorInfo: строка |

## Коды типов значений

| Код | Название |
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
| от 100 до 199 | Пользовательские типы (`CustomMin`, `CustomMax`) |

Unity-пакет использует коды от 100 до 105 для `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int` и `Vector3Int`. Раскладка нагрузок описана в разделе [Сетевой протокол](protocol.md#value-types).

## Перечисления

| Перечисление | Значения |
|---|---|
| `MessageKind` | 1 `OperationRequest`, 2 `OperationResponse`, 3 `Event`, 4 `Ping`, 5 `Pong` |
| `DeliveryMode` | 0 `ReliableOrdered`, 1 `ReliableUnordered`, 2 `UnreliableSequenced`, 3 `Unreliable` |
| `ReceiverGroup` | 0 `Others`, 1 `All`, 2 `MasterClient`, 3 `Targets` |
| `CachingOption` | 0 `DoNotCache`, 1 `AddToRoomCache`, 2 `AddToRoomCacheGlobal`, 3 `RemoveFromRoomCache`, 4 `RemoveFromCacheForActorsLeft` |
| `ObjectOwnershipMode` | 0 `Fixed`, 1 `Takeover`, 2 `Request` |
| `ObjectFlag` | 1 `PersistAcrossScenes` (битовый флаг в `ObjectFlags`) |

## Зарезервированные ключи свойств комнаты

Задавать эти ключи может только мастер-клиент. См. [Безопасность](../server/security.md#reserved-properties).

| Ключ | Константа | Тип | Значение |
|---|---|---|---|
| `_isOpen` | `RoomPropertyKeys.IsOpen` | bool | Могут ли входить новые игроки. |
| `_isVisible` | `RoomPropertyKeys.IsVisible` | bool | Показывается ли комната в списке лобби. |
| `_maxPlayers` | `RoomPropertyKeys.MaxPlayers` | int, от 0 до 255 | Лимит игроков. |
| `_scene` | `RoomPropertyKeys.Scene` | string | Текущая сетевая сцена. Смена удаляет объекты, привязанные к сцене. |

## View id

| Диапазон | Значение |
|---|---|
| от 1 до 999 | Сценовые объекты. Принадлежат мастер-клиенту, пока не назначены другому. |
| от 1000 и выше | Объекты, созданные игроками. `viewId / 1000` равно идентификатору актёра-создателя, так что актёр 3 владеет диапазоном от 3000 до 3999. |
