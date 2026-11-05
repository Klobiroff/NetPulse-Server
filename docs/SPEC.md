# NetPulse — спецификация v0.1

NetPulse — сетевое решение для Unity в духе Photon (PUN + OnPremise Server), но с более простым и понятным API.

- **Сервер**: .NET 10, self-hosted, с плагинами, которые выполняются внутри комнат.
- **Клиент**: Unity-пакет `com.netpulse.unity`, поддерживаются Unity 2021.3 LTS … 6000.x.

Документ — единственный источник правды о контракте. Если в нём не хватает деталей, принимай разумное решение, а затем **допиши его в этот файл** (раздел «Журнал решений» в конце), чтобы второй агент его увидел.

---

## 1. Расположение и владение

| Что | Где | Владелец |
|---|---|---|
| Серверное решение | `G:\C# Projects\NetPulse_Server` | Агент «Server» |
| `NetPulse.Common` (протокол, сериализация) | `G:\C# Projects\NetPulse_Server\src\NetPulse.Common` | Агент «Server» |
| Unity-пакет | `G:\Unity Projects\NetPulse Test\Packages\com.netpulse.unity` | Агент «Client» |
| Копия Common внутри пакета | `Packages\com.netpulse.unity\Runtime\Common\` (генерируется) | создаёт скрипт sync, руками не редактировать |
| Этот документ | `docs\SPEC.md` | общий; пишут только в «Журнал решений» |

Агент «Client» **не правит** файлы в серверном репозитории (исключение — журнал решений в этом документе). Агент «Server» **не правит** Unity-проект (исключение — скрипт sync копирует Common в `Runtime/Common`).

Если нужно изменить API Common, клиентский агент пишет запрос в журнал решений с пометкой `REQUEST(Client→Server)`, а серверный агент проверяет журнал между этапами работы.

## 2. Общие технические ограничения

### NetPulse.Common
- Компилируется **и** как `netstandard2.1` в .NET-решении, **и** как исходники внутри Unity 2021.3 (C# 9).
- Запрещено: file-scoped namespaces, global usings, `record`, `init`, `required`, collection expressions, raw string literals, primary constructors, `static abstract` в интерфейсах, `ImplicitUsings`.
- Можно: `Span<T>`, `ReadOnlySpan<T>`, `System.Buffers.ArrayPool`, `stackalloc`.
- В csproj: `<LangVersion>9.0</LangVersion>`, `<Nullable>disable</Nullable>`, `<ImplicitUsings>disable</ImplicitUsings>`.
- Никаких ссылок на UnityEngine или NuGet-пакеты.

### Транспорт
- **LiteNetLib** (MIT, надёжный UDP) — последняя стабильная версия 1.x. На сервере подключается как NuGet, в Unity исходники той же версии вендорятся в `Runtime/ThirdParty/LiteNetLib/` вместе с LICENSE.
- Весь код NetPulse работает через абстракцию `INetTransport` (в Common), поэтому транспорт можно заменить позже (WebSocket для WebGL, свой reliable-UDP).
- Каналы доставки (`DeliveryMode` в Common): `ReliableOrdered`, `ReliableUnordered`, `UnreliableSequenced`, `Unreliable`.
- Порт по умолчанию: **UDP 5055**.

### Сервер
- `net10.0`, `Nullable enable`, `ImplicitUsings enable`.
- `Microsoft.Extensions.Hosting`, конфиг через `appsettings.json`, логирование через `ILogger`.

## 3. Протокол (реализуется в Common)

Любой пакет на проводе: `[byte MessageKind][payload]`.

| MessageKind | Направление | Payload |
|---|---|---|
| `OperationRequest = 1` | C→S | `byte opCode`, `ushort requestId`, параметры |
| `OperationResponse = 2` | S→C | `byte opCode`, `ushort requestId`, `short returnCode`, `string debugMessage`, параметры |
| `Event = 3` | S→C | `byte eventCode`, параметры |
| `Ping = 4` / `Pong = 5` | ↔ | синхронизация времени (`long clientTicks`, `long serverTicks`) |

**Параметры** — класс `ParameterDictionary` (обёртка над `Dictionary<byte, object>`) с типобезопасными геттерами: `Get<T>(byte key)`, `TryGet<T>`.

Для встроенных операций наружу торчат типизированные структуры (`CreateRoomRequest`, `JoinRoomRequest`, …). Каждая конвертируется в/из `ParameterDictionary` через статические `ToParameters()` / `FromParameters()`. Клиентский и плагинный код с сырыми словарями не работает.

### OpCodes (`public static class OpCode`, значения `byte`)
`Authenticate=1, JoinLobby=2, LeaveLobby=3, CreateRoom=10, JoinRoom=11, JoinOrCreateRoom=12, JoinRandomRoom=13, LeaveRoom=14, RaiseEvent=20, SetRoomProperties=21, SetPlayerProperties=22, ChangeInterestGroups=23, TransferOwnership=24, GetRoomList=30`

### EventCodes (`public static class EventCode`, значения `byte`)
Системные события занимают 200–255, пользовательские — 0–199:
`RoomList=230, PlayerJoined=231, PlayerLeft=232, RoomPropertiesChanged=233, PlayerPropertiesChanged=234, MasterClientChanged=235, OwnershipChanged=236, ObjectInstantiate=240, ObjectDestroy=241, Rpc=242, StateSync=243, ErrorInfo=250`

### ReturnCodes (`public static class ReturnCode`, значения `short`)
`Ok=0, InternalError=-1, InvalidRequest=-2, NotAuthenticated=-3, RoomNotFound=-10, RoomFull=-11, RoomClosed=-12, RoomAlreadyExists=-13, NoMatchFound=-14, PluginReportedError=-20, PluginNotFound=-21, VersionMismatch=-30, InvalidAppId=-31`

### Сериализация (`NetWriter` / `NetReader`)
- Little-endian, без рефлексии.
- Строки: UTF-8 с длиной в формате var-uint.
- Значения типа `object` в параметрах пишутся с префиксом `TypeCode` (byte).
- Встроенные коды: `Null=0, Bool, Byte, Short, Int, Long, Float, Double, String, ByteArray, IntArray, FloatArray, StringArray, ObjectArray, Dictionary (string→object), PropertyTable, Guid`.
- **Пользовательские типы**: `NetTypeRegistry.Register<T>(byte code, Action<NetWriter,T> write, Func<NetReader,T> read)`, коды 100–199. Клиентский пакет регистрирует Unity-типы на кодах `100=Vector2, 101=Vector3, 102=Quaternion, 103=Color, 104=Vector2Int, 105=Vector3Int`. Сервер эти типы знать не обязан: неизвестный пользовательский тип он хранит и пересылает как непрозрачный blob (`OpaqueValue { byte TypeCode; byte[] Data }`), не разбирая его. **Это обязательно.**
- `PropertyTable` — `Dictionary<string, object>` для свойств комнаты и игрока. `null` в значении означает удаление ключа.

### Минимальный публичный API Common (на него клиентский агент пишет код до того, как Common готов)
```csharp
namespace NetPulse.Common
{
    public enum DeliveryMode : byte { ReliableOrdered, ReliableUnordered, UnreliableSequenced, Unreliable }
    public enum MessageKind : byte { OperationRequest = 1, OperationResponse = 2, Event = 3, Ping = 4, Pong = 5 }
    public sealed class NetWriter { /* WriteByte/Bool/Short/UShort/Int/UInt/Long/Float/Double/String/Bytes/VarUInt/Object, Reset(), ToArray(), ArraySegment<byte> AsSegment() */ }
    public sealed class NetReader { /* NetReader(byte[] data, int offset, int count); Read* counterparts, ReadObject(), int Remaining */ }
    public static class NetTypeRegistry { public static void Register<T>(byte code, Action<NetWriter, T> write, Func<NetReader, T> read); }
    public sealed class ParameterDictionary { /* indexer byte→object, Get<T>, TryGet<T>, Count */ }
    public sealed class PropertyTable : IEnumerable<KeyValuePair<string, object>> { /* indexer, Get<T>, TryGet<T>, Remove, Merge(PropertyTable) */ }
    public struct OperationRequest  { public byte OpCode; public ushort RequestId; public ParameterDictionary Parameters; }
    public struct OperationResponse { public byte OpCode; public ushort RequestId; public short ReturnCode; public string DebugMessage; public ParameterDictionary Parameters; }
    public struct EventData         { public byte Code; public ParameterDictionary Parameters; }
    public static class Protocol    { /* Serialize/Deserialize для всех MessageKind; const ushort ProtocolVersion = 1 */ }

    public interface INetTransport  // клиентская сторона
    {
        event Action Connected; event Action<string> Disconnected;
        event Action<ArraySegment<byte>, DeliveryMode> DataReceived;
        bool IsConnected { get; } int RoundTripTimeMs { get; }
        void Connect(string host, int port, string connectionKey);
        void Send(ArraySegment<byte> data, DeliveryMode mode);
        void Poll();          // вызывается из главного потока Unity
        void Disconnect();
    }

    // Типизированные операции — по паре классов на opcode, например:
    public sealed class RoomOptions { public byte MaxPlayers; public bool IsVisible = true; public bool IsOpen = true; public PropertyTable CustomProperties; public string[] LobbyProperties; public string Plugin; public int PlayerTtlMs; public int EmptyRoomTtlMs; }
    public sealed class CreateRoomRequest { public string RoomName; public RoomOptions Options; public PropertyTable PlayerProperties; }
    public sealed class JoinRoomResponse  { public string RoomName; public int LocalActorId; public int MasterClientId; public PlayerInfo[] Players; public PropertyTable RoomProperties; public long ServerTimeTicks; }
    public sealed class PlayerInfo        { public int ActorId; public string NickName; public string UserId; public PropertyTable Properties; public bool IsInactive; }
    public sealed class RoomInfo          { public string Name; public byte PlayerCount; public byte MaxPlayers; public bool IsOpen; public PropertyTable LobbyProperties; }
    public sealed class RaiseEventRequest { public byte EventCode; public object Data; public ReceiverGroup Receivers; public int[] TargetActors; public byte InterestGroup; public CachingOption Caching; public DeliveryMode Delivery; }
    public enum ReceiverGroup : byte { Others, All, MasterClient, Targets }
    public enum CachingOption : byte { DoNotCache, AddToRoomCache, AddToRoomCacheGlobal, RemoveFromRoomCache, RemoveFromCacheForActorsLeft }
}
```
Серверный агент может дополнять API: добавлять методы и поля, а также недостающие типы (`JoinRandomRequest` с фильтром `PropertyTable`, `SetPropertiesRequest` с `expectedProperties` для CAS и т.д.). Переименовывать перечисленное выше без записи в журнал решений нельзя.

### Семантика
- **ActorId** выдаётся комнатой начиная с 1. Мастер-клиент — игрок с наименьшим ActorId; при его выходе мастером становится следующий, и всем рассылается `MasterClientChanged`.
- **Кэш событий комнаты** работает как в Photon: события с `AddToRoomCache` (включая `ObjectInstantiate`) хранятся и повторно отправляются тем, кто заходит позже. Кэшированные события вышедшего игрока удаляются, если не указан `AddToRoomCacheGlobal`.
- **Сетевые объекты**: `ViewId = actorId * 1000 + n`, сценовые объекты используют `actorId = 0`. Сервер ViewId не разбирает, он только маршрутизирует события и ведёт кэш. `ObjectDestroy` удаляет из кэша соответствующий `ObjectInstantiate`.
- **Interest groups**: группа 0 — все; клиент подписывается через `ChangeInterestGroups`.
- **Время сервера**: `ServerTimeTicks` (Stopwatch-подобные миллисекунды с момента старта, `long`) приходит в Join и уточняется пингом каждые 2 с.
- **Лобби**: в `JoinLobby` клиент получает `RoomList` целиком, дальше — дельты раз в ~1 с.

## 4. Сервер — структура

```
NetPulse_Server/
  NetPulse_Server.slnx
  global.json
  docs/SPEC.md
  src/
    NetPulse.Common/                  netstandard2.1 (см. §2)
    NetPulse.Server/                  net10.0 exe — хост (существующий Server/ переносится сюда)
    NetPulse.Plugins/                 net10.0 lib — SDK для авторов плагинов (NuGet-ready)
  plugins/
    NetPulse.Plugins.Sample/          пример: авторитетный счёт + серверный таймер + отказ в событии
  tests/
    NetPulse.Common.Tests/            xUnit: round-trip сериализации, OpaqueValue
    NetPulse.Server.Tests/            xUnit: комнаты, кэш, плагины, интеграционные тесты через реальный UDP
  tools/
    sync-common.ps1                   копирует src/NetPulse.Common/**/*.cs → Unity Runtime/Common/
    NetPulse.TestClient/              консольный .NET-клиент (боты для нагрузки и ручной проверки)
```

### Модель потоков
- Сетевой поток LiteNetLib только раскладывает входящие сообщения по очередям.
- **Каждая комната работает на своём «файбере»** — однопоточной очереди задач (`Channel<Action>`, обрабатываемый одним consumer-таском). Весь код комнаты и плагинов выполняется последовательно, без блокировок, как в Photon. Это ключевое свойство: авторы плагинов не думают о многопоточности.
- Лобби и матчмейкинг работают на отдельном файбере.

### Конфигурация (`appsettings.json`)
`NetPulse:Port`, `MaxConnections`, `AppId` (строка; пусто — проверка выключена), `AppVersionCheck`, `TickRate`, `DisconnectTimeoutMs`, `Plugins:Directory` (по умолчанию `plugins`), `Plugins:Default` (имя плагина для комнат без `Plugin`), `Plugins:Settings:{PluginName}:{key}` (произвольные настройки, передаются плагину).

### Плагины (`NetPulse.Plugins`) — аналог Photon Plugins SDK, но проще
- Плагин — это DLL в `plugins/<PluginName>/` с файлом `plugin.json` (`{ "name", "assembly", "factory" }`). Каждая папка грузится в собственный **collectible `AssemblyLoadContext`**. `NetPulse.Plugins.dll` и `NetPulse.Common.dll` берутся из хоста (shared), чтобы типы совпадали.
- **Hot reload**: при изменении файлов в папке плагина новые комнаты получают новую версию, существующие доигрывают на старой.
- Контракт:
```csharp
public interface IPluginFactory { IRoomPlugin Create(IPluginHost host, string pluginName, IReadOnlyDictionary<string,string> config, out string error); }

public abstract class RoomPlugin : IRoomPlugin   // все методы virtual; по умолчанию вызывают info.Continue()
{
    protected IPluginHost Host { get; }
    public virtual string Name { get; }
    public virtual void OnCreateRoom(ICreateRoomCallInfo info);
    public virtual void OnBeforeJoin(IBeforeJoinCallInfo info);
    public virtual void OnJoin(IJoinCallInfo info);
    public virtual void OnLeave(ILeaveCallInfo info);
    public virtual void OnRaiseEvent(IRaiseEventCallInfo info);
    public virtual void OnBeforeSetProperties(IBeforeSetPropertiesCallInfo info);
    public virtual void OnSetProperties(ISetPropertiesCallInfo info);
    public virtual void OnBeforeCloseRoom(IBeforeCloseRoomCallInfo info);
    public virtual void OnCloseRoom(ICloseRoomCallInfo info);
    public virtual void OnUnhandledException(Exception ex, ICallInfo info);
}
// ICallInfo: Continue(), Fail(string message), Cancel() (только где это имеет смысл), IsProcessed, IsDeferred, Defer()
// Ровно один из вызовов обязателен; если плагин не вызвал ни одного, хост логирует ошибку и выполняет Fail.
// Defer() разрешает асинхронную работу: потом Continue()/Fail() вызывается через Host.Enqueue.

public interface IPluginHost
{
    string RoomName { get; }  IReadOnlyList<IActor> Actors { get; }  int MasterClientId { get; }
    PropertyTable RoomProperties { get; }  ILogger Logger { get; }
    void BroadcastEvent(byte code, object data, ReceiverGroup receivers = ReceiverGroup.All, int[] targets = null, CachingOption caching = CachingOption.DoNotCache, DeliveryMode delivery = DeliveryMode.ReliableOrdered, int senderActorId = 0);
    void SetRoomProperties(PropertyTable props, bool broadcast = true);
    void SetPlayerProperties(int actorId, PropertyTable props, bool broadcast = true);
    void KickActor(int actorId, string reason);
    void CloseRoom();
    IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Action callback); // колбэк выполняется на файбере комнаты
    void Enqueue(Action action);                                                  // вернуться на файбер из async-кода
    Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request);             // результат возвращать через Enqueue
}
```
- Для разбора события в `OnRaiseEvent` есть `info.Request` (`RaiseEventRequest`). Если Data — `OpaqueValue`, плагин может разобрать его сам через `NetReader`.
- Исключение в плагине не роняет сервер: вызывается `OnUnhandledException`, вызов завершается через Fail, комната продолжает работать.
- Отдельно `IAuthPlugin` (глобальный, необязательный): `Task<AuthResult> AuthenticateAsync(AuthRequest)` — custom auth по токену или HTTP.

### Наблюдаемость
Логирование через `ILogger` (консоль). Счётчики `System.Diagnostics.Metrics` (`netpulse.peers`, `netpulse.rooms`, `netpulse.msgs_in/out`). Серверная консольная команда `stats`.

## 4a. Асинхронная модель сервера и плагинов (v0.2, заменяет синхронные хуки и `Defer()`)

Цель — сервер и плагины нигде не блокируют потоки, а медленная операция одного плагина не тормозит другие комнаты, лобби и сетевой поток.

**Файбер (`Fiber`) становится асинхронным.**
- Очередь: `Channel<Func<ValueTask>>`, один consumer. Каждый элемент **дожидается** (`await`), прежде чем начнётся следующий. Так порядок и отсутствие гонок внутри комнаты сохраняются, а `await` внутри комнаты не занимает поток пула.
- Пока элемент выполняется, установлен собственный `FiberSynchronizationContext`. Любое продолжение после `await` в коде комнаты или плагина возвращается на этот файбер. Fire-and-forget продолжение (например, `_ = DoAsync()` в плагине) тоже попадает в очередь файбера, а не выполняется параллельно.
- `[ThreadStatic]` для «текущего файбера» использовать нельзя: признак текущего файбера определяется через `SynchronizationContext.Current` или `AsyncLocal`.
- API: `Post(Func<ValueTask>)`, `Post(Action)`, `Task<T> InvokeAsync<T>(Func<ValueTask<T>>)` (для вызовов из другого файбера, например, когда лобби запрашивает данные у комнаты), `ValueTask DisposeAsync()` (дренирует очередь с таймаутом).
- Защита от флуда — на уровне пира: у каждого `Peer` атомарный счётчик сообщений «в очереди, но не обработано» (лобби и комнаты вместе); лимит `NetPulse:PeerQueueLimit` (по умолчанию 256). Превысивший пир отключается с причиной `Overloaded`, сообщение отбрасывается, метрика `netpulse.peers_overloaded`. `NetPulse:RoomQueueCapacity` (4096) — жёсткая граница очереди пользовательских сообщений на крайний случай: при переполнении сообщение отбрасывается, предупреждение в лог, а отключается пир с наибольшим числом сообщений в этой очереди (не обязательно отправитель). Внутренние сообщения сервера (таймеры, `Enqueue`, `RunInBackground`, shutdown) не ограничены и не учитываются ни за каким пиром. Счётчик освобождается в `finally` (исключения, таймауты хуков) и при отбрасывании элементов при закрытии файбера.

**Хуки плагинов асинхронные.**
```csharp
public abstract class RoomPlugin : IRoomPlugin, IAsyncDisposable
{
    public virtual ValueTask OnCreateRoomAsync(ICreateRoomCallInfo info, CancellationToken ct);
    public virtual ValueTask OnBeforeJoinAsync(IBeforeJoinCallInfo info, CancellationToken ct);
    public virtual ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct);
    public virtual ValueTask OnLeaveAsync(ILeaveCallInfo info, CancellationToken ct);
    public virtual ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct);
    public virtual ValueTask OnBeforeSetPropertiesAsync(IBeforeSetPropertiesCallInfo info, CancellationToken ct);
    public virtual ValueTask OnSetPropertiesAsync(ISetPropertiesCallInfo info, CancellationToken ct);
    public virtual ValueTask OnBeforeCloseRoomAsync(IBeforeCloseRoomCallInfo info, CancellationToken ct);
    public virtual ValueTask OnCloseRoomAsync(ICloseRoomCallInfo info, CancellationToken ct);
    public virtual ValueTask OnUnhandledExceptionAsync(Exception ex, ICallInfo info);
    public virtual ValueTask DisposeAsync();
}
```
- Реализация по умолчанию вызывает `info.Continue()` и возвращает `default` без аллокаций.
- `Continue()` / `Fail()` / `Cancel()` вызываются до завершения хука. Если хук завершился без такого вызова, результат — `Fail` и ошибка в логе (как раньше).
- `Defer()` удаляется: его заменяет `await`.
- **Таймаут хука**: `Plugins:HookTimeoutMs` (по умолчанию 5000). Он передаётся как `CancellationToken`. Если хук не завершился вовремя, вызов завершается через `Fail`, в лог пишется предупреждение, а комната продолжает работу. Поздний `Continue` от «зависшего» хука игнорируется.
- Пока хук комнаты ждёт I/O, эта комната не обрабатывает другие сообщения (порядок важнее). Остальные комнаты, лобби и сеть не затронуты. В README это объясняется, и там же дан совет: долгую работу без влияния на результат вызова запускать фоном через `Host.RunInBackground`.

**`IPluginHost`, дополнения:**
- `IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Func<CancellationToken, ValueTask> callback)` на базе `PeriodicTimer`. Синхронная перегрузка с `Action` остаётся. Колбэки выполняются на файбере, и следующий тик не начнётся, пока не завершился предыдущий.
- `Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct = default)` через `IHttpClientFactory`. После `await` код уже на файбере, поэтому `Enqueue` не нужен.
- `void RunInBackground(Func<CancellationToken, Task> work)` — фоновая работа вне файбера. Её исключения логируются, при закрытии комнаты она отменяется.
- `ValueTask BroadcastEventAsync(...)` не нужен: отправка не блокирует, поэтому `BroadcastEvent` остаётся синхронным.
- `IPluginFactory.CreateAsync(IPluginHost host, string pluginName, IReadOnlyDictionary<string,string> config, CancellationToken ct)` возвращает `ValueTask<PluginCreateResult>`, вместо `out string error`.
- `IAuthPlugin.AuthenticateAsync(AuthRequest request, CancellationToken ct)` с таймаутом `Plugins:AuthTimeoutMs`.

**Сервер в целом:**
- Никаких `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Thread.Sleep` и `lock` вокруг асинхронного кода. Межфиберное взаимодействие идёт через `InvokeAsync` или сообщения.
- Сетевой поток LiteNetLib только парсит пакет и делает `Post` в нужный файбер (лобби или комната).
- Graceful shutdown (`IHostedService.StopAsync`): новые подключения не принимаются, у комнат вызывается `OnBeforeCloseRoomAsync` / `OnCloseRoomAsync` с общим таймаутом, затем `DisposeAsync` плагинов, файберов и выгрузка ALC.
- Нейминг: суффикс `Async`, `CancellationToken` последним параметром, `ConfigureAwait(false)` вне кода, который выполняется на файбере. Внутри файбера `ConfigureAwait(false)` запрещён, иначе потеряется контекст.

## 5. Unity-пакет — структура

```
Packages/com.netpulse.unity/
  package.json                       "unity": "2021.3", name com.netpulse.unity, displayName "NetPulse"
  README.md  CHANGELOG.md  LICENSE.md
  Runtime/
    NetPulse.Runtime.asmdef
    Common/                          ← sync-common.ps1 (отдельный asmdef NetPulse.Common, noEngineReferences: true)
    ThirdParty/LiteNetLib/           ← вендор + свой asmdef
    Core/        NetPulseClient (без MonoBehaviour, чистая логика, тестируемая), LiteNetLibTransport, RequestTracker, ServerTime
    Api/         NetPulse (статический фасад), Room, Player, RoomInfo, NetPulseSettings (ScriptableObject)
    Objects/     NetworkObject, NetworkBehaviour, [NetRpc], RpcTarget, NetStream, INetSerializable, NetworkPrefabRegistry
    Components/  NetworkTransform (интерполяция/экстраполяция), NetworkRigidbody, NetworkAnimator
    Callbacks/   INetPulseConnectionCallbacks, IMatchmakingCallbacks, IRoomCallbacks, ILobbyCallbacks, NetPulseCallbacks (MonoBehaviour с virtual-методами)
    Internal/    NetPulseRunner (скрытый DontDestroyOnLoad: Poll, отправка, тики сериализации), UnityTypeSerializers
  Editor/
    NetPulse.Editor.asmdef
    NetPulseSettingsProvider (Project Settings → NetPulse), NetworkObjectEditor, PrefabRegistry автосборка,
    NetPulse Hub окно (статус соединения, комната, игроки, RTT, трафик в Play Mode)
  Tests/Runtime, Tests/Editor        Unity Test Framework (сериализация Unity-типов, NetPulseClient на фейковом транспорте)
  Samples~/Basic                     сцена: подключение, лобби, комната, спавн игроков с NetworkTransform, RPC, свойства
```

### Принципы API («лучше и понятнее, чем PUN»)
- Один вход: `NetPulse.Connect()` / `NetPulse.Connect(settings)`; состояние в `NetPulse.State` (enum) и событие `NetPulse.StateChanged`.
- Колбэки двумя способами: C#-события (`NetPulse.Events.OnJoinedRoom += …`) **или** наследование от `NetPulseCallbacks`. Регистрация автоматическая в `OnEnable`/`OnDisable`.
- Асинхронные операции возвращают `NetOperation` / `NetOperation<T>`: у него есть `.Completed += …`, а ещё он поддерживает и `yield return` в корутинах (`CustomYieldInstruction`), и `await` (свой awaiter, без зависимости от `Awaitable` из Unity 2023+). Пример: `var room = await NetPulse.JoinOrCreateRoom("arena", options);`. Ошибка — `NetOperation.Error` с `ReturnCode` и сообщением; в `await` выбрасывается `NetPulseException`.
- Спавн: `NetPulse.Instantiate(prefab, pos, rot, data)` принимает **ссылку на префаб**, а не строку из Resources. Префабы регистрируются в `NetworkPrefabRegistry` (ScriptableObject, автосборка в редакторе по GUID). Перегрузка по имени оставлена для совместимости.
- RPC: `[NetRpc] void Hit(int dmg)` + `CallRpc(nameof(Hit), RpcTarget.All, 10)`. Методы индексируются на этапе инициализации (кэш `MethodInfo`; рефлексия только при первом использовании типа). Поддерживается `NetRpcInfo` последним параметром.
- Синхронизация состояния: `NetworkBehaviour.OnSerialize(NetStream stream)` с `stream.IsWriting` или атрибут `[Synced]` для полей примитивных типов. Частота задаётся в `NetPulseSettings.SendRate` / `SerializationRate`. Отправляется только изменившееся — delta по dirty-флагу.
- Ownership: `Fixed`, `Takeover`, `Request` — как в PUN, но через `networkObject.RequestOwnership()` и событие `OwnershipChanged`.
- Офлайн-режим: `NetPulse.OfflineMode = true` имитирует комнату локально (тот же API).

### Совместимость Unity 2021.3 → 6000.x
- C# 9, без `Awaitable`, без `FindObjectsByType` на старых версиях — использовать `#if UNITY_2023_1_OR_NEWER`.
- `Rigidbody.velocity` → `linearVelocity` под `#if UNITY_6000_0_OR_NEWER`.
- Поддерживать **IL2CPP** и code stripping: `[Preserve]` на типах, которые вызываются через рефлексию, и `link.xml` в пакете.
- Не использовать `System.Threading` для логики: всё на главном потоке, `Poll()` в `Update` раннера.
- Domain reload: корректный сброс статического состояния при отключённом domain reload (`[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]`).

## 6. Порядок работ и критерии готовности

**Сервер**
1. S1 — Common + юнит-тесты + `tools/sync-common.ps1`. После этого: создать маркер `src/NetPulse.Common/.api-ready`, запустить sync и записать в журнал `S1 done`.
2. S2 — хост: транспорт, пиры, Authenticate (AppId/версия), лобби, комнаты, свойства, кэш событий, мастер-клиент, interest groups, ping/time.
3. S3 — SDK плагинов, загрузчик ALC, hot reload, пример плагина, тесты.
4. S4 — `NetPulse.TestClient` (боты) + интеграционные тесты; `README.md` (запуск, деплой, написание плагина).
- **Готово, когда**: `dotnet build` и `dotnet test` зелёные, сервер стартует, 2 бота из TestClient заходят в одну комнату и обмениваются событиями через пример-плагин.

**Клиент**
1. C1 — каркас пакета, asmdef, вендор LiteNetLib, `LiteNetLibTransport`, `NetPulseSettings`, раннер. Пока Common не готов, писать код против API из §3; когда появится `.api-ready`, запустить sync и починить компиляцию.
2. C2 — `NetPulseClient` + фасад `NetPulse`: подключение, auth, лобби, матчмейкинг, комнаты, игроки, свойства, события, `NetOperation`, время, офлайн-режим.
3. C3 — NetworkObject/Behaviour, Instantiate/Destroy, RPC, сериализация состояния, ownership, сценовые объекты, компоненты Transform/Rigidbody/Animator.
4. C4 — редактор (Settings provider, Hub, инспекторы, PrefabRegistry), тесты, Samples~/Basic, README.
- **Готово, когда**: пакет компилируется без ошибок и предупреждений NetPulse в Unity 6000.3 (проект `NetPulse Test`), исходники проходят проверочную компиляцию C# 9 / netstandard2.1 против UnityEngine.dll (см. инструкции агента), а EditMode-тесты зелёные.

---

## 7. v0.3 — облегчённый API

Цель — убрать из пользовательского кода строки, байтовые коды и ручную регистрацию типов, не потеряв совместимость с Unity 2021.3 и IL2CPP. Старый низкоуровневый API (`CallRpc(string, …)`, `RaiseEvent(byte, …)`, `[Synced]`, строковые ключи свойств) остаётся рабочим, а в README переезжает в раздел «Низкоуровневый API».

### 7.0 Владение
- **Агент «Net»** (.NET-часть) отвечает за:
  - `src/NetPulse.Common`;
  - сервер;
  - новый проект генератора и анализаторов `src/NetPulse.Unity.Generators` с тестами `tests/NetPulse.Unity.Generators.Tests`;
  - скрипт `tools/build-generators.ps1`;
  - папку `Packages/com.netpulse.unity/Runtime/Analyzers/` (DLL и её `.meta`).
- **Агент «Unity»** отвечает за весь остальной Unity-пакет.
- **Генератор и рантайм** связаны только контрактом из §7.2: генератор вызывает ровно эти имена, Unity-агент их реализует. Менять контракт можно только через журнал решений (`REQUEST` / `ANSWER`).

### 7.1 Генератор — технические рамки
- `netstandard2.0`, **Microsoft.CodeAnalysis.CSharp 3.8.0**, `ISourceGenerator` (не incremental). Unity 2021.3 поддерживает только этот уровень, а 6000.x тоже его понимает.
- Генерируемый код — C# 9, без комментариев, с `// <auto-generated/>` в первой строке (это единственное исключение из правила стиля). Генерировать с `#nullable disable`.
- **Размещение.** DLL лежит в `Runtime/Analyzers/NetPulse.Generators.dll`. В `.meta` стоят `labels: [RoslynAnalyzer]` и PluginImporter, выключенный для всех платформ (Any/Editor/Standalone = false). Папка внутри asmdef `NetPulse.Runtime`, поэтому анализатор действует на `NetPulse.Runtime` и все сборки, которые на неё ссылаются (Assembly-CSharp и пользовательские asmdef). Агент «Net» проверяет это поведение по документации Unity и пишет ссылку в журнал.
- **Стабильные идентификаторы.** `ushort Id = Fold(FNV1a32(utf8(name)))`, где `Fold(h) = (ushort)(h ^ (h >> 16))`. Для RPC `name` — имя метода, перегрузки RPC запрещены. Для сообщений и расширенных типов — полное имя типа (`Namespace.Outer+Inner`). Коллизии ловит анализатор (§7.4) и регистр в рантайме (ошибка в лог).

### 7.2 Контракт «сгенерированный код ↔ рантайм»
Всё служебное помечено `[EditorBrowsable(EditorBrowsableState.Never)]`, namespace — `NetPulse`.

**Сериализация.** Генератор сопоставляет тип поля или параметра с вызовом. Для всех Unity-типов и сетевых ссылок рантайм даёт extension-методы в `public static class NetPulseWriterExtensions` / `NetPulseReaderExtensions`.

| Тип | Запись / чтение |
|---|---|
| bool, byte, short, ushort, int, uint, long, float, double, string, Guid | `WriteBool`/`ReadBool` и т.д. из Common. Недостающие `sbyte`, `ulong`, `char` агент «Net» добавляет в Common |
| enum | как базовый целочисленный тип с кастом |
| Vector2, Vector3, Vector4, Quaternion, Color, Color32, Vector2Int, Vector3Int, Rect | `writer.WriteVector3(v)` / `reader.ReadVector3()` (имена по типу) |
| `Player` | `WritePlayer` / `ReadPlayer` (по ActorId, `null` → 0) |
| `NetworkObject` | `WriteNetworkObject` / `ReadNetworkObject` (по ViewId) |
| `T[]`, `List<T>` поддерживаемого T | `WriteVarUInt(count)` + элементы; `null` → count 0 (null и пустая коллекция на проводе неразличимы, это задокументировано) |
| `[NetSerializable]` / `INetMessage` тип | `T.NetWrite(writer, in value)` / `T.NetRead(reader)` |

**`[NetSerializable] partial struct|class T`** — генератор добавляет:
- `public static void NetWrite(NetWriter writer, in T value)` и `public static T NetRead(NetReader reader)` для всех публичных полей и авто-свойств с `get` и `set`, в порядке объявления;
- регистрацию `NetTypeRegistry.RegisterExtended<T>(id, NetWrite-лямбда, NetRead)` в сгенерированном `static class` с `[UnityEngine.RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` и, под `#if UNITY_EDITOR`, `[UnityEditor.InitializeOnLoadMethod]`.

Тогда тип работает и в `object`-контекстах: свойства, данные событий.

**Common (агент «Net»).**
- `NetTypeRegistry.RegisterExtended<T>(ushort id, Action<NetWriter, T> write, Func<NetReader, T> read)`.
- Новый `NetTypeCode.Extended`. На проводе: `[Extended][ushort id][var-uint length][payload]`.
- Сервер пересылает неизвестный Extended-тип как `OpaqueValue`, не разбирая его.

**RPC.** `[NetRpc(RpcTarget.All)]` на обычном методе с телом внутри `partial`-наследника `NetworkBehaviour`. У атрибута появляются конструктор `NetRpcAttribute(RpcTarget target = RpcTarget.All)` и свойства `Delivery` (DeliveryMode, по умолчанию ReliableOrdered) и `OwnerOnly` (bool: вызов не владельцем логирует предупреждение и не отправляется).

Генератор добавляет в класс:
```csharp
protected new RpcCalls Rpc => new(this, null, null);

public readonly struct RpcCalls
{
    public RpcCalls To(RpcTarget target);
    public RpcCalls To(Player player);
    public void Hit(int damage);
}

protected override bool InvokeGeneratedRpc(ushort rpcId, NetReader reader, in NetRpcInfo info);
```
- `RpcCalls.Hit` выполняет `var w = owner.BeginRpc();`, затем пишет аргументы и вызывает `owner.SendRpc(rpcId, target, player, w, delivery, ownerOnly)`.
- `InvokeGeneratedRpc` делает `switch` по `rpcId`, читает аргументы и вызывает исходный метод. Последний параметр типа `NetRpcInfo` (необязательный) заполняется из `info`. Для неизвестного id метод вызывает `base.InvokeGeneratedRpc`.
- Модификатор `new` у свойства `Rpc` ставится только если у базового класса тоже есть сгенерированный `Rpc`.
- Пользователь пишет `Rpc.Hit(10)` или `Rpc.To(RpcTarget.Others).Hit(10)`.

Рантайм (агент «Unity») реализует в `NetworkBehaviour`:
- `protected NetWriter BeginRpc()` — writer из пула;
- `protected void SendRpc(ushort rpcId, RpcTarget? target, Player player, NetWriter args, DeliveryMode delivery, bool ownerOnly)`. Если `target` равен null, берётся цель из атрибута: генератор передаёт её константой. Если среди получателей есть локальный игрок, метод вызывается локально через тот же путь чтения байтов;
- `protected virtual bool InvokeGeneratedRpc(ushort rpcId, NetReader reader, in NetRpcInfo info) => false`;
- базовый `protected RpcCalls`-подобного свойства у `NetworkBehaviour` нет.

Формат 242 для сгенерированных RPC (`[ViewId][kind = Generated][ushort rpcId][args]`) определяет агент «Unity». Сервер его не разбирает.

**Типизированные сообщения.** Сообщение объявляется как `partial struct|class T : INetMessage`; `INetMessage` — пустой маркер в рантайме. Генератор делает то же, что для `[NetSerializable]`, плюс `NetMessageRegistry.Register<T>(id, write, read)` в том же инициализаторе.

Рантайм реализует:
- `Pulse.Send<T>(in T message, RpcTarget target = RpcTarget.Others)` и `Pulse.Send<T>(in T message, Player player)`; перегрузка с `MessageOptions` (Delivery, Caching);
- `IDisposable Pulse.On<T>(Action<T, Player> handler)`.

На проводе: `EventCode.Message = 244`, `Data = byte[] [ushort id][payload]`.

**`SyncVar<T>`.**
```csharp
private readonly SyncVar<int> _health = new(100);
private readonly SyncVar<string> _title = new("", SyncPermission.Master);
```
- Рантайм: `public sealed class SyncVar<T>` со свойством `Value` (get и set), событием `Changed(T previous, T current)` и свойством `Permission` (enum `SyncPermission { Owner, Master }`).
- Запись без права игнорируется и логирует предупреждение один раз на поле.
- Генератор добавляет `protected override void RegisterSyncVars(SyncVarCollector collector)`: `base.RegisterSyncVars(collector)`, затем `collector.Add(_health, static (w, v) => w.WriteInt(v), static r => r.ReadInt());` в порядке объявления полей.
- Рантайм отправляет изменённые `Owner`-переменные дельтой внутри существующего `StateSync` (243, его сервер принимает только от владельца). `Master`-переменные уходят новым событием `SyncVarSet = 245` (`[ViewId][ushort varIndex][payload]`), его сервер принимает только от мастер-клиента.
- Новому игроку текущие значения приходят полным снимком, так же как сейчас `[Synced]`.

### 7.3 Остальные фичи — только Unity, кроме отмеченного
- **Типизированные ключи (1.4).**
  - Классы `RoomKey<T>` и `PlayerKey<T>` с конструктором `(string name)`.
  - Методы `room.Get(key)`, `room.Set(key, value)`, `room.SetAsync(key, value, expected)` — последний возвращает `NetOperation`, это CAS. Те же методы есть у `player`.
  - `IDisposable room.OnChanged(key, Action<T> handler)` и то же для игрока: `player.OnChanged(key, Action<Player, T>)`.
  - Значение `T` — любой тип, допустимый в `PropertyTable`, включая `[NetSerializable]` через Extended.
- **Сетевой тик (2.3).**
  - Сервер (агент «Net») отдаёт `TickRate` из конфига в `JoinRoomResponse`.
  - Рантайм: `Pulse.TickRate`, `Pulse.Tick` (long, равен `floor(ServerTime * TickRate)`), `Pulse.TickDelta` (`1 / TickRate`).
  - `NetworkBehaviour.OnNetworkTick(long tick)` вызывается раннером с фиксированной частотой: догоняющий цикл, не больше 5 тиков за кадр.
  - `SerializationRate` по умолчанию берётся из `TickRate`, отправка состояния выравнивается по тикам.
- **Хуки жизненного цикла (3.1).**
  - Существующие `OnNetworkSpawned`, `OnNetworkDespawned` и `OnOwnerChanged` остаются.
  - Новые: `OnOwnershipGained()`, `OnOwnershipLost()`, `OnMasterChanged(Player previous, Player current)`, `OnNetworkTick(long)`.
  - Гарантии порядка: `OnNetworkSpawned` вызывается один раз, после `Awake`/`OnEnable` и после применения начального состояния (`SyncVar`, `[Synced]`), до первого `OnNetworkTick`. `OnNetworkDespawned` вызывается до `OnDestroy`. Порядок описывается в README.
- **Async-варианты (3.2).** Все возвращают `NetOperation`, который поддерживает `await`, корутины и `.Completed`.
  - `NetOperation<bool> networkObject.RequestOwnershipAsync(float timeoutSeconds = 5)`: `true`, когда пришёл `OwnershipChanged` на себя; `false` при `Forbidden`, таймауте или в режиме Fixed.
  - `NetOperation<bool> TransferOwnershipAsync(Player)`.
  - `NetOperation Pulse.WaitForPlayersAsync(int count)`.
  - `NetOperation Pulse.WaitUntilAsync(Func<bool> condition)` — проверка раз в кадр.
  - Отмена через `operation.Cancel()`.
- **Сетевая загрузка сцен (4.2).**
  - Рантайм:
    - `Pulse.LoadScene(string sceneName)` и `Pulse.LoadScene(int buildIndex)` — только для мастера; у остальных это предупреждение;
    - `NetPulseSettings.AutoSyncScene` (по умолчанию true).
  - Механизм:
    - мастер ставит свойство комнаты `RoomPropertyKeys.Scene` (`"_scene"`) со значением `"{loadId}:{nameOrIndex}"`;
    - все клиенты, включая опоздавших при входе, загружают сцену асинхронно;
    - пока сцена грузится, входящие события комнаты складываются в очередь и после загрузки проигрываются по порядку;
    - после загрузки регистрируются сценовые `NetworkObject`, мастер вызывает `RegisterSceneObjects`, срабатывает событие `Events.OnNetworkSceneLoaded(string scene)`.
  - `NetworkObject.PersistAcrossScenes` — поле в инспекторе; объект переносится через `DontDestroyOnLoad`. Остальные сетевые объекты уничтожаются локально при смене сцены.
  - Сервер (агент «Net»):
    - `_scene` может ставить только мастер, остальным — `Forbidden`;
    - при смене `_scene` сервер удаляет из реестра и кэша сценовые объекты (`viewId < 1000`) и все объекты без флага persistent;
    - флаг приходит в 240 через новое поле `RaiseEventRequest.ObjectFlags` (byte flags, `PersistAcrossScenes = 1`; по умолчанию 0).
- **Профайлер трафика (5.2).**
  - Рантайм ведёт счётчики байт и сообщений на вход и выход по категориям:
    - `NetworkObject` (ViewId и имя);
    - RPC (`Type.Method`);
    - сообщение (тип);
    - `StateSync`, `SyncVar`, системные.
  - Окно в 1 с даёт байты в секунду, плюс итоги за сессию. Доступ через `Pulse.Stats`.
  - Сбор включается `NetPulseSettings.CollectStats` (по умолчанию: в редакторе и development-сборках). В релизе без dev-флага накладных расходов нет.
  - В NetPulse Hub новая вкладка **Traffic**: таблица с сортировкой, фильтр по категории, кнопка Reset.

### 7.4 Анализаторы (5.3), в той же DLL, что генератор
| Id | Severity | Правило |
|---|---|---|
| NP0001 | Error | Класс с `[NetRpc]`, `SyncVar<T>` или `[NetSerializable]` не `partial` |
| NP0002 | Error | `[NetRpc]` вне наследника `NetworkBehaviour` |
| NP0003 | Error | Неподдерживаемый тип параметра RPC (в сообщении указан тип и список поддерживаемых) |
| NP0004 | Error | RPC возвращает не `void` или имеет `ref`/`out`/`in` параметры |
| NP0005 | Error | Перегрузка RPC с тем же именем |
| NP0006 | Error | Неподдерживаемый тип поля в `[NetSerializable]` / `INetMessage` |
| NP0007 | Error | `SyncVar<T>` с неподдерживаемым T |
| NP0008 | Warning | Поле `SyncVar<T>` не `readonly` или находится вне `NetworkBehaviour` |
| NP0009 | Error | Коллизия id RPC внутри типа или id сообщений / Extended-типов в сборке |
| NP0010 | Error | `[Synced]` на поле неподдерживаемого типа |
| NP0011 | Warning | Вызов `CallRpc("…")` строкой при наличии `[NetRpc]`-метода с таким именем: предлагается `Rpc.X(...)` |

Сообщения анализаторов на английском и с конкретным способом исправления.

### 7.5 Что не входит в v0.3
- Предсказание на клиенте с согласованием (6.4) — отдельная фаза после проверки сетевого тика.
- `SyncList` и `SyncDictionary`.

## Журнал решений
<!-- Формат: YYYY-MM-DD [Server|Client] — решение / REQUEST(A→B) — запрос / ANSWER — ответ -->
- 2026-11-05 [Lead] — Транспорт LiteNetLib за абстракцией INetTransport; комнаты на однопоточных файберах; Common — общий исходный код (netstandard2.1, C# 9), копируется в Unity скриптом sync.
- 2026-11-05 [Client] — Вендорим LiteNetLib **1.3.5** (последний 1.x; тег GitHub 1.3.5). Серверу рекомендуется тот же 1.3.5 (протокол LNL между 1.x совместим, но лучше совпадение).
- 2026-11-05 [Client] — Имя статического фасада: `NetPulse` невозможно (в C# конфликтует с пространством имён `NetPulse` из NetPulse.Common, CS0434 между сборками). Фасад называется **`Pulse`** (`Pulse.Connect()`, `Pulse.JoinOrCreateRoom(...)`, `Pulse.Instantiate(...)`). Остальные типы пакета — в пространстве имён `NetPulse`. Клиентский `RoomInfo` для списка лобби = `NetPulse.Common.RoomInfo` напрямую.
- 2026-11-05 [Server] — S1 done. Common готов (маркер `src/NetPulse.Common/.api-ready`), sync выполнен (`tools/sync-common.ps1`, перезапускать после каждого изменения Common). Серверу нужен LiteNetLib **1.3.5** (согласовано с Client).
- 2026-11-05 [Server] — По CODE_STYLE все DTO (включая `OperationRequest/Response`, `EventData`, `OpaqueValue`) используют свойства `{ get; set; }` с теми же именами. Комментариев/XML-doc в коде нет. Типизированные операции: `ToParameters()` — метод экземпляра, `FromParameters(ParameterDictionary)` — статический.
- 2026-11-05 [Server] — Формат на проводе (детали, которых не было в SPEC): строка = var-uint(byteCount+1) + UTF8, префикс 0 = null; `WriteBytes` = var-uint длина + данные; пользовательский тип 100–199 пишется как `[code][var-uint length][payload]` (так сервер пересылает неизвестные как `OpaqueValue` без разбора; клиенту это прозрачно через `NetTypeRegistry.Register`). Добавлен `NetTypeCode.ParameterDictionary = 17` для вложенных структур. Лимит вложенности 16, длины коллекций проверяются по Remaining.
- 2026-11-05 [Server] — Добавления в Common: `ParamKey` (ключи параметров), `NetSerializationException`, `NetReader.ReadKind` -> `Protocol.ReadKind/ReadOperationRequest/ReadOperationResponse/ReadEvent/ReadPingPong`, `Protocol.Serialize(NetWriter, X)` (сам делает Reset и возвращает ArraySegment), `Protocol.SerializePingPong`, `Protocol.DefaultPort=5055`, `Protocol.DefaultConnectionKey="NetPulse"`. `ParameterDictionary.Get<T>` возвращает default при отсутствии/неконвертируемости, числовые типы конвертируются между собой. `PropertyTable.Matches(expected)` — CAS. ReturnCode добавлены: `NotInRoom=-4, AlreadyInRoom=-5, Forbidden=-6, PropertyCasFailed=-15, ActorNotFound=-16, AuthenticationFailed=-32`. `OpCode.JoinOrCreateRoom=12` (в SPEC — JoinOrCreateRoom). Типизированные запросы: AuthenticateRequest/Response, JoinLobbyRequest, CreateRoomRequest, JoinRoomRequest, JoinOrCreateRoomRequest, JoinRandomRequest (ExpectedProperties, ExpectedMaxPlayers, CreateOptions), JoinRoomResponse (+MaxPlayers/IsOpen/IsVisible/IsRejoin), LeaveRoomRequest(BecomeInactive), SetPropertiesRequest(ActorId; 0 = комната; Properties; ExpectedProperties; Broadcast), ChangeInterestGroupsRequest(Add/Remove), TransferOwnershipRequest, RoomListData (событие RoomList и ответ GetRoomList), события: RoomEventData (Sender, Data, ViewId), PlayerLeftData, PropertiesChangedData, OwnershipChangedData, ErrorInfoData; PlayerJoined = параметры `PlayerInfo.ToParameters()`; MasterClientChanged = `ParamKey.MasterClientId`.
- 2026-11-05 [Server] — `RaiseEventRequest` получил поле `ViewId` (int): сервер не разбирает Data, поэтому ViewId нужен для кэша (`ObjectInstantiate` хранится по ViewId, `ObjectDestroy` его удаляет). Клиент обязан заполнять ViewId для событий 240/241. Пользовательское событие доходит до клиентов как `EventData{Code=eventCode, Parameters=RoomEventData}`; игрок, не вышедший из кэша, получает кэш сразу после ответа на Join. Rejoin: если в комнате есть неактивный игрок с тем же UserId, JoinRoom/JoinOrCreate возвращает его ActorId (`IsRejoin=true`).
- 2026-11-05 [Server] — S2–S4 done. LiteNetLib на сервере и в TestClient: **1.3.5** (NuGet), `Microsoft.Extensions.Hosting` 10.0.14. Конфиг плагинов лежит под `NetPulse:Plugins:*` (`Directory`, `Default`, `Auth`, `HotReload`, `Settings:{Plugin}:{key}`); каталог плагинов относительный = от папки сервера. Connection key = `NetPulse:ConnectionKey` (по умолчанию `Protocol.DefaultConnectionKey = "NetPulse"`).
- 2026-11-05 [Server] — Клиентам важно: (1) ответ на `RaiseEvent` приходит только если `RequestId != 0` или при ошибке (иначе тишина); (2) `Ping` отвечает `Pong(clientTicks, serverMs)`; (3) в `JoinRoomResponse` сначала приходит ответ, затем кэшированные события и OwnershipChanged, `PlayerJoined` рассылается остальным (не самому вошедшему); (4) свойства комнаты/игрока рассылаются ВСЕМ, включая отправителя, события `RoomPropertiesChanged/PlayerPropertiesChanged` несут `PropertiesChangedData` (дельта, `null` = удалено); (5) зарезервированные свойства комнаты `RoomPropertyKeys.IsOpen/IsVisible/MaxPlayers` (`_isOpen`, `_isVisible`, `_maxPlayers`) меняет только мастер; (6) клиент не может поднимать события 200–255, кроме 240–243; (7) выход игрока: `PlayerLeft{ActorId, IsInactive, MasterClientId}`; при закрытии комнаты оставшимся приходит `ErrorInfo{ErrorCode=RoomClosed}`; кик — `ErrorInfo{Forbidden}`; (8) `JoinRandomRequest.ExpectedProperties` сверяется только с `RoomOptions.LobbyProperties`; если комнат нет и задан `CreateOptions`, создаётся комната со случайным именем; (9) Rejoin — по тому же `UserId`, пока игрок inactive (`PlayerTtlMs != 0`).
- 2026-11-05 [Server] — Плагины: `plugin.json` может содержать `"auth": "Type"` (реализация `IAuthPlugin`); общие сборки хоста: NetPulse.Plugins, NetPulse.Common, Microsoft.Extensions.Logging.Abstractions. Пример плагина по умолчанию деплоится в `src/NetPulse.Server/bin/<Config>/net10.0/plugins/NetPulse.Plugins.Sample` post-build'ом. Добавлено в Common: `RoomPropertyKeys`.
- 2026-11-05 [Client] — C1–C4 done. Пакет `com.netpulse.unity` 0.1.0 собран; проверочная компиляция (netstandard2.1, C# 9, Runtime+Editor+Tests+Samples, с UNITY_2023_1+/UNITY_6000+ и без) — 0 ошибок, 0 предупреждений. Клиент использует: `ParamKey`/типизированные запросы Common как есть; события 240–243 идут через RaiseEvent с `Data = byte[]` (формат — внутренний у клиента) и `ViewId`; RPC (242) и StateSync (243) тоже несут ViewId; запрос владения у владельца идёт событием 242 (kind=1, Targets=[owner]); смена владельца — op TransferOwnership + событие OwnershipChanged. Клиент опирается на: ответ на Create/Join/JoinOrCreate/JoinRandom = `JoinRoomResponse`, `PlayerJoined` не приходит самому себе, `PlayerPropertiesChanged/RoomPropertiesChanged` несут `Sender` (клиент игнорирует эхо от себя).
- 2026-11-05 [Server] — §4a реализован. `Fiber`: один consumer, каждый элемент `await`-ится; `FiberSynchronizationContext` возвращает продолжения на файбер (очередь продолжений обрабатывается между шагами, поэтому код комнаты может чередоваться на `await`, но никогда не идёт параллельно); `IsCurrent` через `SynchronizationContext.Current`; `TryPost` (с лимитом `NetPulse:RoomQueueCapacity`, 4096) → при переполнении пир отключается с причиной `Overloaded`; `Post` (внутренние сообщения без лимита), `InvokeAsync`, `DisposeAsync(timeout)`. Хуки `Async` + `CancellationToken`, `Plugins:HookTimeoutMs` (5000) → Fail + warning, поздний `Continue` игнорируется; `Defer()` удалён; `IPluginFactory.CreateAsync` → `PluginCreateResult`; `IAuthPlugin.AuthenticateAsync(request, ct)` с `Plugins:AuthTimeoutMs` (5000) → `AuthenticationFailed`; `IPluginHost`: async `CreateTimer` (PeriodicTimer, без пересечений), `HttpAsync(request, ct)` через `IHttpClientFactory`, `RunInBackground`. Graceful shutdown: `NetPulse:ShutdownTimeoutMs` (10000), комнаты закрываются через `OnBeforeCloseRoomAsync/OnCloseRoomAsync`, затем `DisposeAsync` плагинов и выгрузка ALC. Common не менялся (sync не нужен); TestClient теперь показывает в причине дисконнекта доп. данные (`...:Overloaded`).
- 2026-11-05 [Server] — Флуд-защита сделана по-пирово (см. §4a): `Peer.InFlight` (Interlocked), `FloodGuard.TryPost`, `NetPulse:PeerQueueLimit`=256, метрика `netpulse.peers_overloaded`; `Fiber.TryPost(work, owner, release, out heaviest)` учитывает только пользовательские сообщения (`Post` для внутренних не ограничен); при глобальном переполнении отключается пир с максимумом сообщений в очереди этого файбера. Прежнее поведение «отключать того, кто пишет при полной очереди» убрано.
- 2026-11-05 [Lead] — NetPulse.Common.asmdef: autoReferenced = true (было false). Пользовательский код в Assembly-CSharp использует PropertyTable/RoomInfo/RoomOptions; при false импортированный Samples~/Basic не компилировался.
- 2026-11-05 [Client] — IL2CPP: link.xml внутри пакета Unity не читает (документация Unity), поэтому клиент генерирует `Library/NetPulse/link.xml` через `IUnityLinkerProcessor` (включая preserve=all для сборок NetPulse); `[NetRpc]`/`[Synced]` наследуют `PreserveAttribute`. Пакет 0.1.1. Файл Common asmdef не трогаю (autoReferenced=true оставлен).
- 2026-11-05 [Server] — Сервер валидирует владение сетевыми объектами. Common (синхронизирован, asmdef сохранён): `ObjectOwnershipMode {Fixed, Takeover, Request}`; `RaiseEventRequest.OwnershipMode` (ParamKey 44, по умолчанию Fixed — старый клиент работает, но объекты не передаются); `OpCode.RequestOwnership=25`, `OpCode.RegisterSceneObjects=26`; `EventCode.OwnershipRequested=237`; `ReturnCode.ObjectNotFound=-17`; типы `RequestOwnershipRequest{ViewId}`, `RegisterSceneObjectsRequest{ViewIds,Modes}`, `OwnershipRequestedData{ViewId,RequesterActorId}`. Семантика: реестр на комнату (viewId→создатель, владелец, режим, сцена); 240 допустим только при `viewId>=1000 && viewId/1000==sender` и если viewId свободен; 243 — только от текущего владельца; 241 — владелец или мастер; сценовые объекты (1..999) принадлежат мастеру (`owner 0` = «мастер»), режим по умолчанию Fixed, мастер задаёт режимы через RegisterSceneObjects; неизвестный viewId для 241/243 молча отбрасывается. Нарушения: сообщение отбрасывается, `Forbidden` (если RequestId!=0) или `ErrorInfo` (не чаще 1/с на игрока), метрика `netpulse.ownership_violations`, дисконнекта нет. Передачи: Fixed — запрещено всем; Takeover — любой берёт себе, владелец/мастер назначают любого; Request — назначают только владелец/мастер, остальные шлют RequestOwnership → владельцу приходит событие 237 → одобряет вызовом TransferOwnership(requester); без ответа через `NetPulse:OwnershipRequestTimeoutMs` (5000) запрос сгорает. Каждое изменение → `OwnershipChanged` всем (включая отправителя); поздним игрокам после кэша реплеится текущий владелец. Выход игрока: уничтожаются только объекты, которыми он владеет сейчас (вместе с кэшем), переданные остаются, сценовые возвращаются мастеру (`OwnershipChanged(viewId, 0)`), inactive-игроки сохраняют объекты. Плагины: `Host.GetNetworkObject/NetworkObjects`, хук `OnBeforeTransferOwnershipAsync`. Исправление: `ObjectDestroy` теперь всегда доставляется и чистит кэш по ViewId независимо от `Caching` (раньше `RemoveFromRoomCache` на 241 приводил к тому, что событие не доставлялось).
- 2026-11-05 [Server] — CLIENT TODO (ownership): (1) в `NetworkObjectManager.Instantiate` передавать `RaiseEventOptions.OwnershipMode = no.OwnershipMode` → `RaiseEventRequest.OwnershipMode` для события 240 (в `RaiseRoomEvent` пробросить поле). (2) Не применять владельца локально до ответа сервера: убрать `no.ChangeOwner(...)` из `ApplyAndSendOwnership`; применять изменение только по `OwnershipChanged`, включая случай `sender == LocalActorId` (убрать этот пропуск в `OnOwnershipChanged`). `NewOwnerActorId == 0` для сценового объекта означает «владелец — мастер». (3) `RequestOwnership` для `Request`-режима отправлять опом `OpCode.RequestOwnership` (`RequestOwnershipRequest{ViewId}`), а не RPC-ом 242; для `Takeover` тоже слать `RequestOwnership` (или `TransferOwnership(viewId, self)`), для `Fixed` ничего не слать. Удалить `RpcKindOwnershipRequest`. (4) Обрабатывать событие `EventCode.OwnershipRequested` (237, `OwnershipRequestedData`) у владельца: вызвать `behaviour.OnOwnershipRequested(requester)`; если одобрено — `TransferOwnership(viewId, requesterActorId)`; отказ = ничего не делать. (5) `Destroy`: слать 241 как раньше (`ViewId` заполнен); `Caching` можно оставить `RemoveFromRoomCache`, сервер чистит кэш сам. (6) Сценовые объекты (`SceneId != 0`): после входа в комнату мастер вызывает `RegisterSceneObjects(viewIds[], modes[])` для всех сценовых `NetworkObject` (иначе они Fixed и принадлежат мастеру); `StateSync` для них отправляет текущий владелец (мастер, пока не передан). (7) Обрабатывать `Forbidden`/`ObjectNotFound` в ответах и `ErrorInfo` (логировать, откатывать локальные предположения). (8) `OnPlayerLeft` на клиенте: уничтожать только объекты, чей текущий владелец вышел (как уже сделано через `OwnerActorId`) — серверная логика совпадает; сценовые объекты вышедшего владельца становятся `OwnerActorId=0`. (9) Аутентификация: токен передаётся в `ConnectionOptions.AuthToken` → `AuthenticateRequest.AuthToken`; после ответа брать `UserId`/`NickName` из `AuthenticateResponse` (сервер подставляет авторитетные значения); при `AuthenticationFailed` сервер разорвёт соединение через ~250 мс.
- 2026-11-05 [Server] — AuthSample: `plugins/NetPulse.Plugins.AuthSample` (имя плагина `AuthSample`, режимы Hmac и Http, настройки `NetPulse:Plugins:Settings:AuthSample:{Mode,Secret,Url}`, включается `NetPulse:Plugins:Auth=AuthSample`). SDK: `IAuthPluginContext` (Logger + HttpAsync через IHttpClientFactory) передаётся вторым параметром конструктора auth-типа; `plugin.json` может содержать только `auth` без `factory`. Если `Plugins:Auth` задан, но плагин не загрузился, сервер не стартует (fail closed). После `AuthenticationFailed` сервер дисконнектит пир через 250 мс. Генератор токенов: `NetPulse.TestClient --make-token --secret S --user U --nick N --ttl-minutes 60`; бот принимает `--token`.
- 2026-11-05 [Client] — Ownership TODO 1–9 done (пакет 0.2.0). (1) 240 несёт `OwnershipMode` + `ViewId`; (2) владелец меняется только по `OwnershipChanged` (в т.ч. от самого себя; offline-режим применяет сразу); (3) `RequestOwnership` = `OpCode.RequestOwnership` для Request и Takeover, Fixed ничего не шлёт, RPC-путь удалён; (4) 237 → `NetworkBehaviour.OnOwnershipRequest(Player)` + `Events.OnOwnershipRequest`, одобрение = `networkObject.ApproveOwnership(player)` (TransferOwnership), отказ = бездействие; (5) 241 как раньше; (6) мастер после входа и после смены мастера вызывает `RegisterSceneObjects` (повтор на смене мастера безвреден: реестр живёт на сервере); (7) `Forbidden`/`ObjectNotFound`/`ErrorInfo` → warning + `Events.OnOwnershipFailed`, локально ничего не предполагалось, повторные `ErrorInfo` логируются не чаще 1/с; (8) при выходе игрока уничтожаются только объекты, которыми он владеет, сценовые его объекты получают `OwnerActorId=0`; (9) `ConnectionOptions.AuthToken`/`Pulse.AuthToken` → `AuthenticateRequest.AuthToken`, `UserId`/`NickName` берутся из `AuthenticateResponse`. Проверено e2e на реальном сервере (3–4 клиента): Takeover, Fixed→Forbidden, Request→237→approve, StateSync не-владельца не доставляется, переданный объект переживает создателя, сценовый объект возвращается мастеру. Не проверено: Unity-слой (NetworkObject/Manager) и режим AuthSample (Hmac) на живом сервере.
