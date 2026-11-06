# Подключение

`Pulse` является статической точкой входа клиента. Она называется не `NetPulse`, потому что `NetPulse` это ещё и пространство имён, а в C# тип и пространство имён с одинаковым именем нельзя использовать одновременно. Подключиться можно одним вызовом, который возвращает операцию, доступную для `await`.

```csharp
using NetPulse;
using UnityEngine;

public sealed class ConnectOnStart : MonoBehaviour
{
    private async void Start()
    {
        Pulse.NickName = "Alice";
        Pulse.StateChanged += state => Debug.Log($"State: {state}");

        try
        {
            await Pulse.Connect();
            Debug.Log($"Connected as {Pulse.UserId}, ping {Pulse.RoundTripTimeMs} ms");
        }
        catch (NetPulseException e)
        {
            Debug.LogError($"Connect failed ({e.ReturnCode}): {e.Message}");
        }
    }
}
```

`Pulse.Connect()` читает `NetPulseSettings` (см. [настройки и редактор](settings-and-editor.md)), открывает UDP-соединение и проходит аутентификацию. Операция завершается, когда состояние становится `Connected`. Если `Pulse.NickName` не задан, клиент берёт `Default Nick Name` из настроек, а если и он пуст, то `Player` со случайным числом.

Операцию можно использовать и в корутине, и с колбэком. Полностью `NetOperation` описан на странице [async](async.md).

```csharp
using System.Collections;
using NetPulse;
using UnityEngine;

public sealed class ConnectInCoroutine : MonoBehaviour
{
    private IEnumerator Start()
    {
        var operation = Pulse.Connect();
        yield return operation;

        if (operation.HasError)
        {
            Debug.LogError(operation.Error.ToString());
        }
    }
}
```

## Настройки подключения {#connection-settings}

`Pulse.Connect()` использует ассет настроек из `Resources`. Передайте собственный `NetPulseSettings`, чтобы заменить его для этого и последующих вызовов:

```csharp
using NetPulse;
using UnityEngine;

public sealed class ConnectWithSettings : MonoBehaviour
{
    private async void Start()
    {
        var settings = ScriptableObject.CreateInstance<NetPulseSettings>();
        settings.Host = "game.example.com";
        settings.Port = 5055;
        settings.AppId = "my-game";
        settings.AppVersion = "1.2";
        await Pulse.Connect(settings);
    }
}
```

`Pulse.Connect(settings)` ещё и становится активным `NetPulseSettings.Instance`, поэтому частоты, реестр префабов и остальные поля берутся из него. Третья перегрузка принимает объект `ConnectionOptions` с полями `Host`, `Port`, `ConnectionKey`, `AppId`, `AppVersion`, `UserId`, `NickName`, `AuthToken` и `RequestTimeout`. Она полностью обходит ассет настроек, что удобно для тестов и инструментов:

```csharp
using NetPulse;
using UnityEngine;

public sealed class ConnectWithOptions : MonoBehaviour
{
    private async void Start()
    {
        var options = new ConnectionOptions
        {
            Host = "127.0.0.1",
            Port = 5055,
            AppId = "my-game",
            AppVersion = "1.2",
            NickName = "Bot"
        };
        await Pulse.Connect(options);
    }
}
```

`ConnectionKey` служит общим секретом, который проверяет UDP-рукопожатие. По умолчанию он равен `NetPulse` и должен совпадать с ключом, заданным на сервере.

В настройках есть и `Run In Background`: когда опция включена, `Pulse.Connect` выставляет `Application.runInBackground`, чтобы соединение не рвалось при потере фокуса окна. Выключить эту опцию он не может.

## Идентификация {#identity}

Вместе с рукопожатием клиент отправляет `UserId`. По умолчанию это случайный идентификатор, который создаётся один раз и хранится в `PlayerPrefs` под ключом `NetPulse.UserId`, так что одна и та же установка узнаётся между сессиями. Если `Persist User Id` выключен, при каждом `Connect` используется новый идентификатор. Когда работает плагин аутентификации, итоговые `UserId` и `NickName` определяет ответ сервера, и клиент их принимает (`Pulse.UserId`, `Pulse.Client.NickName`).

## Токен аутентификации

По умолчанию сервер принимает любого клиента. Если на нём запущен плагин аутентификации (см. [аутентификация](../plugins/authentication.md)), передайте токен до подключения:

```csharp
using NetPulse;
using UnityEngine;

public sealed class ConnectWithToken : MonoBehaviour
{
    [SerializeField] private string _token;

    private async void Start()
    {
        Pulse.AuthToken = _token;
        await Pulse.Connect();
    }
}
```

Отклонённый токен завершает операцию `Connect` ошибкой `AuthenticationFailed` (-32), а сервер разрывает соединение. Токен сбрасывается при перезагрузке домена и при новом запуске Play Mode, поэтому задавайте его при каждом запуске.

## Состояния и события {#states-and-events}

`Pulse.State` имеет тип `NetState`. `Pulse.IsConnected` истинно во всех состояниях, кроме `Disconnected`, `Connecting` и `Authenticating`, так что оно остаётся истинным, пока вы в лобби или в комнате.

| Состояние | Когда |
|---|---|
| `Disconnected` | Начальное состояние; после `Disconnect()`, неудачного подключения или потери соединения |
| `Connecting` | После `Connect()`, пока не поднято UDP-соединение |
| `Authenticating` | Соединение есть, сервер проверяет `AppId`, версию протокола и токен |
| `Connected` | Аутентификация пройдена, клиент вне лобби и комнат |
| `InLobby` | После `JoinLobby()` |
| `JoiningRoom` | Пока ожидается ответ на создание или вход |
| `InRoom` | После успешного создания или входа |
| `Leaving` | Пока ожидается ответ на `LeaveRoom()` |

`NetState.Disconnecting` объявлено на будущее и текущим клиентом не выставляется. `Connect()` в состоянии, отличном от `Disconnected`, сразу завершается ошибкой `InvalidState`.

Каждое уведомление доступно в трёх равноценных формах. Берите ту, что удобнее для скрипта:

1. События C# в `Pulse.Events`, например `Pulse.Events.OnJoinedRoom += Handler;`.
2. `MonoBehaviour`, наследующий `NetPulseCallbacks`. Он сам регистрируется в `OnEnable`, снимает регистрацию в `OnDisable`, а вы переопределяете только нужные методы.
3. Любой объект, который реализует один или несколько интерфейсов колбэков и зарегистрирован через `Pulse.AddCallbackTarget(this)` и `Pulse.RemoveCallbackTarget(this)`.

```csharp
using System.Collections.Generic;
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class NetworkLog : NetPulseCallbacks
{
    public override void OnConnected() => Debug.Log("Connected");

    public override void OnDisconnected(string cause) => Debug.Log($"Disconnected: {cause}");

    public override void OnRoomListUpdate(IReadOnlyList<RoomInfo> rooms) => Debug.Log($"{rooms.Count} rooms");
}
```

```csharp
using NetPulse;
using UnityEngine;

public sealed class RoomWatcher : MonoBehaviour, IRoomCallbacks
{
    private void OnEnable() => Pulse.AddCallbackTarget(this);

    private void OnDisable() => Pulse.RemoveCallbackTarget(this);

    public void OnPlayerEnteredRoom(Player player) => Debug.Log($"{player.NickName} entered");

    public void OnPlayerLeftRoom(Player player) => Debug.Log($"{player.NickName} left");

    public void OnRoomPropertiesChanged(NetPulse.Common.PropertyTable changed)
    {
    }

    public void OnPlayerPropertiesChanged(Player player, NetPulse.Common.PropertyTable changed)
    {
    }

    public void OnMasterClientSwitched(Player newMaster) => Debug.Log($"New master: {newMaster.NickName}");
}
```

Колбэки выполняются в главном потоке. Исключение в одном обработчике попадает в лог, остальные обработчики всё равно вызываются. Полный список:

| Событие в `Pulse.Events` | Интерфейс | Аргументы |
|---|---|---|
| `OnStateChanged` | нет (используйте `Pulse.StateChanged`) | `NetState` |
| `OnConnected` | `INetPulseConnectionCallbacks` | нет |
| `OnDisconnected` | `INetPulseConnectionCallbacks` | `string cause` |
| `OnJoinedLobby`, `OnLeftLobby` | `ILobbyCallbacks` | нет |
| `OnRoomListUpdate` | `ILobbyCallbacks` | `IReadOnlyList<RoomInfo>` |
| `OnCreatedRoom`, `OnJoinedRoom`, `OnLeftRoom` | `IMatchmakingCallbacks` | нет |
| `OnCreateRoomFailed`, `OnJoinRoomFailed`, `OnJoinRandomFailed` | `IMatchmakingCallbacks` | `short returnCode, string message` |
| `OnPlayerEnteredRoom`, `OnPlayerLeftRoom` | `IRoomCallbacks` | `Player` |
| `OnRoomPropertiesChanged` | `IRoomCallbacks` | `PropertyTable changed` |
| `OnPlayerPropertiesChanged` | `IRoomCallbacks` | `Player, PropertyTable changed` |
| `OnMasterClientSwitched` | `IRoomCallbacks` | `Player newMaster` |
| `OnOwnershipRequest` | `IOwnershipCallbacks` | `NetworkObject, Player requester` |
| `OnOwnershipChanged` | `IOwnershipCallbacks` | `NetworkObject, Player previous, Player current` |
| `OnOwnershipFailed` | `IOwnershipCallbacks` | `NetworkObject, short returnCode, string message` |
| `OnNetworkSceneLoaded` | `ISceneCallbacks` | `string sceneName` |
| `OnEvent` | `IOnEventCallback` | `byte code, int senderActorId, object data` |

`NetPulseCallbacks` реализует все эти интерфейсы, только его методы виртуальные и пустые. `OnEvent` передаёт лишь ваши собственные коды событий от 0 до 199 (см. [низкоуровневый API](low-level-api.md)).

## Отключение и причины отключения

`Pulse.Disconnect()` закрывает соединение с вашей стороны. Событие `OnDisconnected` (как и неудачные операции) несёт строку с причиной:

| Причина | Источник |
|---|---|
| `Client disconnect` | Вы вызвали `Pulse.Disconnect()` или приложение закрылось |
| `Timeout` | 10 секунд не приходило ни одного пакета |
| `ConnectionFailed` | На рукопожатие не пришёл ответ |
| `HostUnreachable`, `NetworkUnreachable`, `UnknownHost` | Адрес недоступен или не разрешился |
| `RemoteConnectionClose` | Сервер закрыл соединение (исключение игрока, остановка, отклонённый токен) |
| `ConnectionRejected` | Сервер отказал в рукопожатии, например из-за неверного ключа подключения |
| `InvalidProtocol` | Сервер использует другую версию транспортного протокола |
| `Failed to open a UDP socket` | Не удалось открыть локальный сокет |
| Сообщение сервера | При ошибке аутентификации передаётся текст сервера (например, для неверного `AppId`) |

Большинство строк совпадает с именами `DisconnectReason` из LiteNetLib. При обрыве все ожидающие операции завершаются ошибкой `NetPulseReturnCode.Disconnected` (-101), комната и список комнат очищаются, сетевые объекты локально уничтожаются, а состояние возвращается в `Disconnected`. Если вы были в комнате, сначала вызывается `OnLeftRoom`, затем `OnDisconnected`.

Чтобы переподключиться, снова вызовите `Pulse.Connect()`. Обычный приём: запускать его из `OnDisconnected` после короткой паузы.

## Ошибки операций {#operation-errors}

У каждого запроса есть тайм-аут (`Request Timeout`, по умолчанию 10 секунд). Ошибки на стороне клиента используют `NetPulseReturnCode`:

| Код | Имя | Значение |
|---|---|---|
| -100 | `Timeout` | Сервер не ответил вовремя |
| -101 | `Disconnected` | Соединение потеряно или его не было |
| -102 | `InvalidState` | Вызов недопустим в текущем состоянии, например вход в комнату при `Disconnected` |

Ответы сервера используют коды из раздела [коды и ключи](../reference/codes.md), например `AuthenticationFailed` (-32), `VersionMismatch` (-30) и `InvalidAppId` (-31). Операция с `await` бросает `NetPulseException` с полем `ReturnCode`; у остальных есть свойство `Error`.

## Качество связи и замена транспорта

`Pulse.RoundTripTimeMs` показывает время кругового пробега по UDP-каналу, а `Pulse.ServerTime` даёт оценку серверных часов (см. [тики и время](ticks-and-time.md)).

`Pulse.UseTransport(INetTransport)` подменяет транспорт, например на WebSocket для WebGL. Вызывать его нужно в отключённом состоянии и до `Connect()`. Он сбрасывает клиент, поэтому колбэки регистрируйте после него. Транспорт по умолчанию: `LiteNetLibTransport`.
