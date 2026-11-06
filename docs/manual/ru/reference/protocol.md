# Сетевой протокол

Здесь описано, что ходит между клиентом и сервером, для тех, кто пишет собственный клиент. Пользователям Unity это знать не нужно. Все числа относятся к версии протокола 1 (`Protocol.ProtocolVersion`). Имена взяты из `NetPulse.Common`, библиотеки, которая компилируется на .NET Standard 2.1 и входит в Unity-пакет. Клиент на .NET может использовать её напрямую.

## Транспорт

NetPulse отправляет сообщения по UDP через LiteNetLib 1.3.5. Собственный клиент обязан говорить на протоколе LiteNetLib, поэтому на практике это клиент на .NET с той же версией LiteNetLib.

- Клиент подключается к UDP-порту сервера (по умолчанию 5055) с ключом подключения (по умолчанию `NetPulse`). Ключ проверяет транспорт.
- Каждое сообщение NetPulse укладывается в один пакет LiteNetLib, отправленный по каналу по умолчанию.
- Режим доставки пакета сопоставляется с методом LiteNetLib:

| `DeliveryMode` | Значение | Метод LiteNetLib |
|---|---|---|
| `ReliableOrdered` | 0 | `ReliableOrdered` |
| `ReliableUnordered` | 1 | `ReliableUnordered` |
| `UnreliableSequenced` | 2 | `Sequenced` |
| `Unreliable` | 3 | `Unreliable` |

Ответы и большинство событий сервер отправляет через `ReliableOrdered`. События, которые поднимают клиенты или плагины, уходят с тем `Delivery`, который запросил отправитель. Pong отправляется как `Unreliable`.

- Сервер отключает пира, молчащего дольше `DisconnectTimeoutMs` (по умолчанию 10 секунд). Продолжайте вызывать `PollEvents`: он отправляет служебные keep-alive сообщения транспорта.
- Когда сервер отключает клиента намеренно, он кладёт причину в UTF-8 в данные отключения. Известные причины: `Overloaded` и `AuthenticationFailed`.

## Примитивы

Все многобайтовые числа хранятся в порядке little-endian.

| Название | Кодирование |
|---|---|
| `byte`, `bool` | 1 байт. У bool значение 0 или 1. |
| `short`, `ushort` | 2 байта. |
| `int`, `uint`, `float` | 4 байта. |
| `long`, `ulong`, `double` | 8 байт. |
| `varuint` | По 7 бит на байт, сначала младшие, старший бит означает «далее ещё байты». Не больше 5 байт. |
| `string` | `varuint` (число байт UTF-8 плюс 1), затем сами байты. Префикс 0 означает `null`, префикс 1 означает пустую строку. |
| `bytes` | Длина в `varuint`, затем байты. |
| `Guid` | 16 байт в раскладке .NET. |

## Формат сообщений

Первый байт каждого пакета задаёт вид сообщения.

| Вид | Значение | Раскладка после байта вида |
|---|---|---|
| OperationRequest | 1 | `opCode: byte`, `requestId: ushort`, `parameters` |
| OperationResponse | 2 | `opCode: byte`, `requestId: ushort`, `returnCode: short`, `debugMessage: string`, `parameters` |
| Event | 3 | `code: byte`, `parameters` |
| Ping | 4 | `clientTicks: long`, `serverTicks: long` |
| Pong | 5 | `clientTicks: long`, `serverMs: long` |

Ответ несёт `opCode` и `requestId` своего запроса. `returnCode` 0 означает успех, остальные коды перечислены в разделе [Коды и ключи](codes.md). `debugMessage` содержит текст для человека.

В пинге отправьте свои часы в `clientTicks` и 0 в `serverTicks`. Сервер ответит вашим значением и своим временем в миллисекундах с момента запуска. Вычитанием можно оценить время кругового обхода и смещение часов.

Идентификаторы запросов выбираете вы. Для каждого запроса, на который нужен ответ, берите ненулевой идентификатор. Сервер отвечает на любую операцию, кроме `RaiseEvent`: на неё ответ приходит, только если идентификатор ненулевой или событие завершилось ошибкой.

## Параметры

`parameters` представляет собой словарь из однобайтового ключа в значение:

```text
varuint count
count x ( key: byte, value )
```

Ключами служат константы `ParamKey`, они перечислены в разделе [Коды и ключи](codes.md#parameter-keys). Отсутствующие ключи читаются как значения по умолчанию. Сервер преобразует числовые типы при чтении параметра, поэтому `int` на месте `byte` сработает, если значение помещается.

## Типы значений {#value-types}

Каждое значение начинается с байта кода типа, за которым идёт полезная нагрузка.

| Код | Тип | Нагрузка |
|---|---|---|
| 0 | null | нет |
| 1 | bool | 1 байт |
| 2 | byte | 1 байт |
| 3 | short | 2 байта |
| 4 | int | 4 байта |
| 5 | long | 8 байт |
| 6 | float | 4 байта |
| 7 | double | 8 байт |
| 8 | string | `string` |
| 9 | byte[] | `bytes` |
| 10 | int[] | число в `varuint`, затем по 4 байта на элемент |
| 11 | float[] | число в `varuint`, затем по 4 байта на элемент |
| 12 | string[] | число в `varuint`, затем столько значений `string` |
| 13 | object[] | число в `varuint`, затем столько значений |
| 14 | dictionary | число в `varuint`, затем пары (ключ `string`, значение) |
| 15 | PropertyTable | та же раскладка, что у словаря |
| 16 | Guid | 16 байт |
| 17 | ParameterDictionary | блок параметров (число, затем пары ключ и значение) |
| 18 | extended | `ushort` идентификатор, длина в `varuint`, затем нагрузка |
| от 100 до 199 | пользовательский | длина в `varuint`, затем нагрузка |

Вложенность значений не больше 16 уровней. Если заявленное число или длина больше оставшихся в пакете данных, пакет считается некорректным.

Коды от 100 до 199 и 18 несут прикладные типы, например векторы. Перед ними стоит длина, чтобы сервер мог хранить и пересылать неизвестный ему тип: он сохраняет байты как `OpaqueValue` и записывает их обратно без изменений. Unity-пакет регистрирует `Vector2` (100), `Vector3` (101), `Quaternion` (102), `Color` (103), `Vector2Int` (104) и `Vector3Int` (105). В .NET свои типы регистрируются через `NetTypeRegistry.Register` (коды от 100 до 199) или `NetTypeRegistry.RegisterExtended` (идентификаторы от 0 до 65535).

Некорректный пакет записывается в лог и игнорируется.

## Сессия

1. Подключитесь с ключом подключения.
2. Отправьте `Authenticate` (операция 1) с AppId, версией протокола, идентификатором пользователя, никнеймом и необязательным токеном. Пока это не удалось, любая другая операция возвращает `NotAuthenticated`.
3. По желанию отправьте `JoinLobby` (операция 2). Сервер ответит и затем пришлёт полный список комнат событием 230. Следующие события 230 несут изменения (`IsFullList` равен false) с интервалом `RoomListIntervalMs`. `GetRoomList` (операция 30) возвращает полный список прямо в ответе.
4. Создайте комнату или войдите в неё операциями 10, 11, 12 или 13. У пира может выполняться только один вход одновременно.
5. Внутри комнаты используйте операции с 14 по 26. События приходят по мере возникновения.

### Операции и их параметры

| Операция | Название | Параметры |
|---|---|---|
| 1 | Authenticate | `AppId` (str), `ProtocolVersion` (int), `AppVersion` (str), `UserId` (str), `NickName` (str), `AuthToken` (str), `AuthData` (PropertyTable) |
| 2 | JoinLobby | `LobbyName` (str) |
| 3 | LeaveLobby | нет |
| 10 | CreateRoom | `RoomName`, `Options` (ParameterDictionary), `PlayerProperties` (PropertyTable) |
| 11 | JoinRoom | `RoomName`, `PlayerProperties` |
| 12 | JoinOrCreateRoom | как у CreateRoom |
| 13 | JoinRandomRoom | `ExpectedProperties`, `ExpectedMaxPlayers` (byte), `PlayerProperties`, `Options` (используется, если комнату придётся создать) |
| 14 | LeaveRoom | `BecomeInactive` (bool) |
| 20 | RaiseEvent | `EventCode` (byte), `Data`, `Receivers` (byte), `TargetActors` (int[]), `InterestGroup` (byte), `Caching` (byte), `Delivery` (byte), `ViewId` (int), `OwnershipMode` (byte), `ObjectFlags` (byte) |
| 21 | SetRoomProperties | `Properties`, `ExpectedProperties`, `Broadcast` (bool, по умолчанию true) |
| 22 | SetPlayerProperties | `ActorId` (0 означает вас самого), `Properties`, `ExpectedProperties`, `Broadcast` |
| 23 | ChangeInterestGroups | `GroupsToAdd` (byte[]), `GroupsToRemove` (byte[]) |
| 24 | TransferOwnership | `ViewId`, `NewOwnerActorId` |
| 25 | RequestOwnership | `ViewId` |
| 26 | RegisterSceneObjects | `ViewIds` (int[]), `OwnershipModes` (byte[]) |
| 30 | GetRoomList | нет |

`Options` при создании комнаты передаётся блоком параметров с полями `MaxPlayers` (byte, 0 означает без ограничения), `IsVisible`, `IsOpen`, `Properties` (начальные пользовательские свойства комнаты), `LobbyProperties` (string[] ключей свойств, которые показываются в списке комнат), `Plugin`, `PlayerTtlMs` и `EmptyRoomTtlMs`.

`PlayerTtlMs` и `EmptyRoomTtlMs` задаются в миллисекундах. При `PlayerTtlMs`, отличном от 0, игрок, который отключился или вышел с `BecomeInactive`, остаётся в комнате неактивным актёром и может вернуться с тем же `UserId`. Положительное значение удаляет неактивного игрока через это время, отрицательное хранит его до возвращения. Положительный `EmptyRoomTtlMs` удерживает пустую комнату живой на это время.

### Ответы

Успешный `Authenticate` возвращает `UserId`, `NickName`, `ServerTimeTicks` (миллисекунды с запуска сервера, несмотря на название) и `ProtocolVersion`.

Операции входа (с 10 по 13) отвечают состоянием комнаты либо кодом ошибки без параметров:

| Ключ | Содержимое |
|---|---|
| `RoomName` | Имя комнаты. |
| `ActorId` | Ваш идентификатор актёра. |
| `MasterClientId` | Идентификатор актёра мастера. |
| `Players` | `object[]` блоков параметров, по одному на актёра: `ActorId`, `NickName`, `UserId`, `Properties`, `IsInactive`. |
| `RoomProperties` | Свойства комнаты, включая зарезервированные ключи. |
| `ServerTimeTicks` | Миллисекунды сервера. |
| `MaxPlayers`, `IsOpen`, `IsVisible` | Настройки комнаты. |
| `IsRejoin` | Истина, если вы заняли место неактивного актёра. |
| `TickRate` | Настройка `TickRate` сервера. |

После ответа на вход сервер присылает кэшированные события, затем событие `OwnershipChanged` для каждого объекта, чей владелец отличается от создателя. Остальные игроки получают `PlayerJoined` с блоком вашего игрока.

## События

Событие несёт код и блок параметров. Коды до 199 отведены под ваши события, а объектные события с 240 по 245 тоже поднимают клиенты. Они приходят с такой нагрузкой:

| Ключ | Содержимое |
|---|---|
| `Sender` | Идентификатор актёра-отправителя (0 для сервера). |
| `Data` | Значение, которое передал отправитель. |
| `ViewId` | View id, если он есть. |

У собственных событий сервера нагрузка другая:

| Код | Название | Ключи нагрузки |
|---|---|---|
| 230 | RoomList | `IsFullList`, `Rooms` (`object[]` блоков с `RoomName`, `PlayerCount`, `MaxPlayers`, `IsOpen`, `Properties`), `RemovedRooms` (string[]) |
| 231 | PlayerJoined | блок игрока |
| 232 | PlayerLeft | `ActorId`, `IsInactive`, `MasterClientId` |
| 233 | RoomPropertiesChanged | `ActorId` (0), `Properties` (дельта, значение null означает удаление), `Sender` |
| 234 | PlayerPropertiesChanged | `ActorId`, `Properties`, `Sender` |
| 235 | MasterClientChanged | `MasterClientId` |
| 236 | OwnershipChanged | `ViewId`, `NewOwnerActorId` (0 у сценового объекта означает «мастер»), `Sender` |
| 237 | OwnershipRequested | `ViewId`, `RequesterActorId`, отправляется владельцу |
| 250 | ErrorInfo | `ErrorCode`, `Message` |

Изменения свойств комнаты и игроков идут всем игрокам, включая того, кто их сделал.

### Получатели, кэш и группы

`Receivers`: 0 Others, 1 All, 2 MasterClient или 3 Targets (идентификаторы актёров из `TargetActors`). Ненулевая `InterestGroup` ограничивает доставку игроками, которые вошли в эту группу через `ChangeInterestGroups`; группа 0 доходит до всех.

`Caching`: 0 DoNotCache, 1 AddToRoomCache, 2 AddToRoomCacheGlobal, 3 RemoveFromRoomCache или 4 RemoveFromCacheForActorsLeft. Кэшированные события воспроизводятся игрокам, которые входят позже. События `AddToRoomCache` удаляются, когда их отправитель уходит окончательно, а события `AddToRoomCacheGlobal` остаются. Удаление из кэша убирает записи с тем же кодом события и тем же `ViewId`, если вы его передали.

### Сетевые объекты

Объектные события передаются обычными вызовами `RaiseEvent` со специальным кодом и `ViewId`. Сервер проверяет их так, как описано в разделе [Безопасность](../server/security.md#ownership-validation): для создания (240) нужно, чтобы `ViewId / 1000` равнялось вашему идентификатору актёра, а режим передаётся в `OwnershipMode` (0 Fixed, 1 Takeover, 2 Request); бит 0 в `ObjectFlags` помечает объект, который переживает смену сцены. Удаление (241) убирает объект и его кэшированные события. RPC (242), синхронизация состояния (243), сообщение (244) и мастерская sync-переменная (245) пересылаются после своих проверок. Формат `Data` у этих событий определяет клиент, для сервера он непрозрачен.

Когда меняется свойство комнаты `_scene`, сервер удаляет все сценовые объекты и все объекты, не помеченные как постоянные, вместе с их кэшированными событиями.

## Минимальный клиент

Эта программа на .NET подключается, аутентифицируется и входит в комнату с помощью `NetPulse.Common` и LiteNetLib 1.3.5:

```xml title="WireClient.csproj"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="LiteNetLib" Version="1.3.5" />
    <ProjectReference Include="path\to\NetPulse_Server\src\NetPulse.Common\NetPulse.Common.csproj" />
  </ItemGroup>

</Project>
```

```csharp title="MinimalClient.cs"
using LiteNetLib;
using NetPulse.Common;

namespace WireExamples;

public sealed class MinimalClient
{
    private readonly NetWriter _writer = new();
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private NetPeer? _server;
    private ushort _nextRequestId;

    public MinimalClient()
    {
        _manager = new NetManager(_listener);
        _listener.PeerConnectedEvent += OnConnected;
        _listener.NetworkReceiveEvent += OnReceive;
    }

    public void Run(string host, int port, TimeSpan duration)
    {
        _manager.Start();
        _manager.Connect(host, port, Protocol.DefaultConnectionKey);
        var end = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < end)
        {
            _manager.PollEvents();
            Thread.Sleep(15);
        }

        _manager.Stop();
    }

    private void OnConnected(NetPeer peer)
    {
        _server = peer;
        var login = new AuthenticateRequest { UserId = "demo", NickName = "Demo" };
        Send(OpCode.Authenticate, login.ToParameters());
    }

    private void Send(byte opCode, ParameterDictionary parameters)
    {
        var request = new OperationRequest { OpCode = opCode, RequestId = ++_nextRequestId, Parameters = parameters };
        var packet = Protocol.Serialize(_writer, request);
        _server!.Send(packet.Array!, packet.Offset, packet.Count, DeliveryMethod.ReliableOrdered);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var data = new NetReader(reader.GetRemainingBytesSegment());
        var kind = Protocol.ReadKind(data);

        if (kind == MessageKind.OperationResponse)
        {
            var response = Protocol.ReadOperationResponse(data);
            Console.WriteLine($"response op={response.OpCode} request={response.RequestId} code={response.ReturnCode}");

            if (response.OpCode == OpCode.Authenticate && response.ReturnCode == ReturnCode.Ok)
            {
                Send(OpCode.JoinOrCreateRoom, new JoinOrCreateRoomRequest { RoomName = "wire-demo" }.ToParameters());
            }
        }
        else if (kind == MessageKind.Event)
        {
            var received = Protocol.ReadEvent(data);
            Console.WriteLine($"event code={received.Code}");
        }

        reader.Recycle();
    }
}
```

Запустите её из файла верхнего уровня `Program.cs`:

```csharp title="Program.cs"
using WireExamples;

new MinimalClient().Run("127.0.0.1", 5055, TimeSpan.FromSeconds(2));
```

Если запустить её против работающего сервера, она печатает:

```text
response op=1 request=1 code=0
response op=12 request=2 code=0
```

`Protocol.Serialize` возвращает сегмент внутреннего буфера записи, поэтому отправляйте его раньше, чем снова используете writer.
