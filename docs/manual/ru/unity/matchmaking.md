# Подбор комнат

Подбор комнат определяет, как игроки оказываются в одной комнате: просматривают список комнат в лобби, заходят в комнату по имени, создают свою или просят сервер выбрать случайную комнату под ваш фильтр. Каждый вызов возвращает `NetOperation<Room>`, поэтому его можно ждать через `await`. Вызывайте их после завершения `Pulse.Connect()` ([подключение](connecting.md)).

## Лобби и список комнат

Входить в лобби необязательно. Делайте это, когда нужен живой список комнат:

```csharp
using System.Collections.Generic;
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class RoomBrowser : NetPulseCallbacks
{
    private async void Start()
    {
        await Pulse.Connect();
        await Pulse.JoinLobby();
    }

    public override void OnRoomListUpdate(IReadOnlyList<RoomInfo> rooms)
    {
        foreach (var room in rooms)
        {
            var map = room.LobbyProperties?.Get<string>("map");
            Debug.Log($"{room.Name} {room.PlayerCount}/{room.MaxPlayers} open={room.IsOpen} map={map}");
        }
    }
}
```

`JoinLobby()` работает из состояния `Connected` и переводит клиента в `InLobby`. Сервер сразу присылает полный список, а затем изменения примерно раз в секунду. Клиент их сливает, и `Pulse.LobbyRooms` всегда хранит актуальный список, отсортированный по имени. После каждого обновления вызывается `OnRoomListUpdate` с этим объединённым списком.

Если нужен только снимок, вызовите `GetRoomList()` из `Connected` или `InLobby`. Он возвращает `NetOperation<IReadOnlyList<RoomInfo>>`. `LeaveLobby()` останавливает обновления и очищает `LobbyRooms`.

У `RoomInfo` есть поля `Name`, `PlayerCount`, `MaxPlayers`, `IsOpen` и `LobbyProperties`. В списке только видимые комнаты. В `LobbyProperties` попадают лишь те пользовательские свойства, ключи которых создатель комнаты перечислил в `RoomOptions.LobbyProperties`. Так список остаётся небольшим, и по тому же набору фильтруется случайный подбор (см. ниже).

Вход в комнату из лобби переводит клиента в `InRoom`. После выхода из комнаты состояние `Connected`, поэтому чтобы снова получать список, вызовите `JoinLobby()` ещё раз.

## RoomOptions {#roomoptions}

`RoomOptions` (в `NetPulse.Common`) описывает комнату при создании:

| Свойство | По умолчанию | Значение |
|---|---|---|
| `MaxPlayers` | `0` | Лимит игроков, `0` означает без ограничения. Тип `byte`, поэтому не больше 255. |
| `IsVisible` | `true` | Показывать ли комнату в списке комнат. |
| `IsOpen` | `true` | Можно ли новым игрокам входить. |
| `CustomProperties` | нет | Начальные свойства комнаты ([комнаты и свойства](rooms-and-properties.md)). |
| `LobbyProperties` | нет | Ключи из `CustomProperties`, которые копируются в `RoomInfo.LobbyProperties`. |
| `Plugin` | нет | Имя серверного плагина, который ведёт эту комнату ([плагины](../plugins/overview.md)). |
| `PlayerTtlMs` | `0` | Сколько сервер хранит место отключившегося игрока. См. ниже. |
| `EmptyRoomTtlMs` | `0` | Сколько живёт пустая комната. `0` закрывает её сразу. |

Позже комната может менять `IsOpen`, `IsVisible` и `MaxPlayers` через зарезервированные свойства комнаты `_isOpen`, `_isVisible` и `_maxPlayers`, которые разрешено устанавливать только мастер-клиенту. Например, `room.SetProperty("_isOpen", false)` закрывает вход после начала матча.

## Создание, вход, вход или создание

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class JoinExample : MonoBehaviour
{
    private async void Start()
    {
        await Pulse.Connect();

        var options = new RoomOptions
        {
            MaxPlayers = 4,
            CustomProperties = new PropertyTable { ["map"] = "forest", ["mode"] = "ctf" },
            LobbyProperties = new[] { "map", "mode" }
        };

        var room = await Pulse.JoinOrCreateRoom("arena", options);
        Debug.Log($"{room.Name}: {room.PlayerCount} players, master is {room.MasterClient.NickName}");
    }
}
```

| Вызов | Поведение | Типичные коды ошибок |
|---|---|---|
| `CreateRoom(name, options, playerProperties)` | Создаёт комнату. Пустое имя означает, что имя выберет сервер. | `RoomAlreadyExists` |
| `JoinRoom(name, playerProperties)` | Входит в существующую комнату. | `RoomNotFound`, `RoomFull`, `RoomClosed` |
| `JoinOrCreateRoom(name, options, playerProperties)` | Входит в комнату, если она есть, иначе создаёт её с `options`. | `RoomFull`, `RoomClosed` |
| `JoinRandomRoom(expectedProperties, expectedMaxPlayers, createOptions, playerProperties)` | Входит в случайную подходящую комнату. | `NoMatchFound` |

Все аргументы после имени необязательны. `playerProperties` передаётся как `PropertyTable` и становится свойствами локального игрока в момент входа, так что остальные клиенты видят их уже в `OnPlayerEnteredRoom`. Коды относятся к `ReturnCode` из `NetPulse.Common`; полный список приведён в разделе [коды и ключи](../reference/codes.md).

Успешный вызов завершается объектом `Room`. К этому моменту заполнены `Pulse.CurrentRoom`, `Pulse.LocalPlayer` и список игроков, серверные часы синхронизированы, а состояние `InRoom`. События приходят в таком порядке: `OnCreatedRoom` (только если этот вызов создал комнату), затем `OnJoinedRoom`, затем завершается операция. Неудачный вызов поднимает одно из событий `OnCreateRoomFailed`, `OnJoinRoomFailed` или `OnJoinRandomFailed` с кодом и сообщением, а операция с `await` бросает `NetPulseException`. Пользователь может находиться только в одной комнате, и попытка войти, когда вы уже в комнате, завершается ошибкой.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class JoinWithErrors : MonoBehaviour
{
    private async void Start()
    {
        await Pulse.Connect();

        try
        {
            await Pulse.JoinRoom("arena");
        }
        catch (NetPulseException e) when (e.ReturnCode == ReturnCode.RoomNotFound)
        {
            await Pulse.CreateRoom("arena", new RoomOptions { MaxPlayers = 4 });
        }
        catch (NetPulseException e)
        {
            Debug.LogError($"Join failed ({e.ReturnCode}): {e.Message}");
        }
    }
}
```

## Вход в случайную комнату

`JoinRandomRoom` рассматривает видимые, открытые и незаполненные комнаты. Из них остаются те, чьи `LobbyProperties` совпадают с `expectedProperties` (каждый указанный ключ должен быть равен) и чей `MaxPlayers` равен `expectedMaxPlayers` (`0` означает любой), затем случайно выбирается одна и делается попытка войти. Если ничего не подошло, создаётся новая комната с `createOptions` и сгенерированным именем, а при `createOptions` равном `null` вызов завершается ошибкой `NoMatchFound`.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class QuickMatch : MonoBehaviour
{
    private async void Start()
    {
        await Pulse.Connect();

        var filter = new PropertyTable { ["mode"] = "ctf" };
        var fallback = new RoomOptions
        {
            MaxPlayers = 8,
            CustomProperties = new PropertyTable { ["mode"] = "ctf" },
            LobbyProperties = new[] { "mode" }
        };

        var room = await Pulse.JoinRandomRoom(filter, 0, fallback);
        Debug.Log($"Playing in {room.Name}");
    }
}
```

Фильтр видит только lobby-свойства. Ключ, по которому вы фильтруете, должен быть перечислен в `RoomOptions.LobbyProperties` тех комнат, которые вы хотите найти, как `mode` выше.

## Выход и возвращение {#leaving-and-rejoining}

```csharp
await Pulse.LeaveRoom();
```

`LeaveRoom()` возвращает клиента в `Connected`. Все сетевые объекты комнаты локально уничтожаются, срабатывает `OnLeftRoom`. Вызов вне комнаты завершается ошибкой `InvalidState`.

`LeaveRoom(becomeInactive: true)` и потеря соединения не удаляют вас, а резервируют место, если комната создана с `PlayerTtlMs`, отличным от `0`. Положительное значение хранит место столько миллисекунд. Остальные клиенты получают `OnPlayerLeftRoom`, где `player.IsInactive` равно `true`, а ваши объекты остаются в комнате. Если тот же `UserId` вызовет `JoinRoom` для этой комнаты до истечения срока, сервер вернёт прежний `ActorId`, и остальные клиенты снова получат `OnPlayerEnteredRoom`. При `PlayerTtlMs`, равном `0`, выход всегда удаляет игрока, а принадлежащие ему объекты уничтожаются у всех.

Чтобы возвращение работало, `UserId` должен оставаться прежним, поэтому не выключайте `Persist User Id` ([подключение](connecting.md#identity)).

## Комната, в которой вы находитесь

После входа из `Pulse` доступно следующее:

| Член | Значение |
|---|---|
| `Pulse.IsInRoom` | Истинно в состоянии `InRoom` |
| `Pulse.CurrentRoom` | Объект `Room` или `null` |
| `Pulse.LocalPlayer`, `Pulse.LocalActorId` | Вы сами |
| `Pulse.IsMasterClient` | Являетесь ли вы мастером |

О `Room` и `Player` рассказывает страница [комнаты и свойства](rooms-and-properties.md). Чтобы дождаться нужного числа игроков, используйте `Pulse.WaitForPlayersAsync` ([async](async.md)).
