# Комнаты и свойства

Когда вы вошли в комнату, `Pulse.CurrentRoom` возвращает `Room`, а каждый участник, включая вас, представлен объектом `Player`. И у комнаты, и у игрока могут быть свойства: небольшие значения, которые хранит сервер, которые может читать любой участник и которые получают опоздавшие при входе.

## Room и Player

Члены `Room`:

| Член | Значение |
|---|---|
| `Name`, `MaxPlayers`, `IsOpen`, `IsVisible` | Настройки комнаты (см. [подбор комнат](matchmaking.md#roomoptions)) |
| `MasterClientId`, `MasterClient` | Текущий мастер |
| `LocalPlayer` | Вы |
| `Players`, `PlayerCount` | Все игроки, упорядоченные по `ActorId`; неактивные тоже входят |
| `GetPlayer(actorId)`, `TryGetPlayer(actorId, out player)` | Поиск по номеру актора |
| `GetNextPlayer(after)` | Игрок со следующим большим `ActorId`, после последнего возвращается первый |
| `Properties` | Сырой `PropertyTable` |

Члены `Player`: `ActorId`, `NickName`, `UserId`, `IsLocal`, `IsInactive`, `IsMasterClient` и `Properties`.

```csharp
using NetPulse;
using UnityEngine;

public sealed class RoomPrinter : MonoBehaviour
{
    private void Print()
    {
        var room = Pulse.CurrentRoom;
        if (room == null)
        {
            return;
        }

        foreach (var player in room.Players)
        {
            var tags = (player.IsMasterClient ? " master" : string.Empty) + (player.IsLocal ? " you" : string.Empty);
            Debug.Log($"#{player.ActorId} {player.NickName}{tags}");
        }
    }
}
```

## Типизированные ключи

`RoomKey<T>` или `PlayerKey<T>` связывает имя свойства с типом значения, так что компилятор проверяет и то, и другое. Объявляйте каждый ключ один раз как поле `static readonly` и используйте везде одно и то же.

```csharp
using System;
using NetPulse;
using UnityEngine;

public sealed class Scores : MonoBehaviour
{
    private static readonly RoomKey<int> Score = new("score");
    private static readonly PlayerKey<string> Mood = new("mood");

    private IDisposable _scoreWatch;
    private IDisposable _moodWatch;

    private void OnEnable() => Pulse.Events.OnJoinedRoom += Subscribe;

    private void OnDisable()
    {
        Pulse.Events.OnJoinedRoom -= Subscribe;
        _scoreWatch?.Dispose();
        _moodWatch?.Dispose();
    }

    public void AddPoint()
    {
        var room = Pulse.CurrentRoom;
        room.Set(Score, room.Get(Score) + 1);
    }

    private void Subscribe()
    {
        var room = Pulse.CurrentRoom;
        _scoreWatch = room.OnChanged(Score, value => Debug.Log($"Score is {value}"));
        _moodWatch = Pulse.LocalPlayer.OnChanged(Mood, (player, value) => Debug.Log($"{player.NickName} feels {value}"));
        Pulse.LocalPlayer.Set(Mood, "happy");
    }
}
```

У `Room` и `Player` API одной формы:

| Вызов | Значение |
|---|---|
| `Get(key, defaultValue)` | Читает локальную копию. Если ключа нет или значение не приводится к типу, возвращает `defaultValue` (или `default`). |
| `Set(key, value)` | Записывает. Локальная копия и обработчики `OnChanged` обновляются сразу, а изменение уходит на сервер и остальным клиентам. |
| `SetAsync(key, value, expected)` | Сравнить и записать (compare-and-set). Возвращает `NetOperation`. См. ниже. |
| `OnChanged(key, handler)` | Подписка на изменения одного ключа. Возвращает `IDisposable`, который отписывает. |

Для `PlayerKey<T>` обработчик получает `Player` и новое значение. `OnChanged` срабатывает и на ваши собственные записи, и на изменения других клиентов, когда они приходят. Он вызывается для ключа только тогда, когда этот ключ входит в изменение.

Числа приводятся между числовыми типами, поэтому ключ `RoomKey<int>` прочитает значение, которое другой клиент сохранил как `long`. Отсутствующий ключ читается как значение по умолчанию.

### Кто может писать

Любой участник может писать свойства комнаты и свойства самого себя. Свойства другого игрока разрешено менять только мастер-клиенту. Зарезервированные ключи комнаты `_isOpen`, `_isVisible`, `_maxPlayers` и `_scene` тоже доступны только мастеру. `Set` применяет изменение локально до ответа сервера и об отказе не сообщает; если сервер его отклонил, локальная копия сохраняет отклонённое значение. Когда важно знать, принята ли запись, используйте форму compare-and-set, которая дожидается сервера.

Запись `null` удаляет ключ. Типизированный `Set` не может передать `null` для типа-значения, поэтому удаляйте через сырой API: `room.SetProperty("score", null)`.

## Compare-and-set

`SetAsync(key, value, expected)` срабатывает, только если сохранённое значение всё ещё равно `expected`. Сервер проверяет и применяет изменение атомарно, а локальная копия меняется лишь после согласия сервера. Несовпадение завершает операцию ошибкой `PropertyCasFailed` (-15). Так несколько клиентов могут занять слот или увеличить счётчик, не затирая друг друга.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class ClaimHost : MonoBehaviour
{
    private static readonly RoomKey<string> Host = new("host");
    private static readonly RoomKey<int> Round = new("round");

    private async void Claim()
    {
        var room = Pulse.CurrentRoom;

        try
        {
            await room.SetAsync(Host, Pulse.LocalPlayer.NickName, expected: null);
            Debug.Log("You are the host");
        }
        catch (NetPulseException e) when (e.ReturnCode == ReturnCode.PropertyCasFailed)
        {
            Debug.Log($"{room.Get(Host)} got there first");
        }
    }

    private async void NextRound()
    {
        var room = Pulse.CurrentRoom;
        var current = room.Get(Round);
        await room.SetAsync(Round, current + 1, current);
    }
}
```

Отсутствующий ключ никогда не равен числу. Если `round` не задавали, `SetAsync(Round, 1, 0)` завершится ошибкой, потому что сервер сравнивает «нет значения» с `0`. Создайте комнату с этим ключом в `CustomProperties`, чтобы он существовал, либо используйте `null` как `expected` через сырой API, описанный дальше.

## Сырой PropertyTable {#the-raw-propertytable}

Типизированные ключи построены поверх строковых ключей и `PropertyTable` (из `NetPulse.Common`). Сырой API пригодится, когда ключи собираются во время работы или когда нужно записать несколько свойств одним запросом.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class RawProperties : MonoBehaviour
{
    private async void Start()
    {
        var room = Pulse.CurrentRoom;

        room.SetProperty("map", "forest");
        var map = room.GetProperty<string>("map", "unknown");

        var delta = new PropertyTable { ["round"] = 2, ["phase"] = "fight" };
        var expected = new PropertyTable { ["round"] = 1 };
        await Pulse.Client.SetRoomProperties(delta, expected);

        Pulse.LocalPlayer.SetProperty("team", 1);
        var team = Pulse.LocalPlayer.GetProperty<int>("team");
        Debug.Log($"{map} team {team} {room.Properties.Count} properties");
    }
}
```

| Вызов | Значение |
|---|---|
| `room.SetProperty(key, value)`, `player.SetProperty(key, value)` | Записывают один ключ. `null` удаляет его. |
| `room.SetProperties(delta, expected)`, `player.SetProperties(delta)` | Записывают несколько ключей одним запросом. `expected` превращает запись в compare-and-set; каждый ключ из `expected` должен совпасть, а ключ со значением `null` совпадает с отсутствующим. |
| `GetProperty<T>(key, defaultValue)` | Читает один ключ. |
| `Pulse.Client.SetRoomProperties(delta, expected)`, `Pulse.Client.SetPlayerProperties(player, delta, expected)` | То же самое, но возвращают `NetOperation`, который можно ждать. |
| `Properties` | Сам `PropertyTable`: `Get<T>`, `TryGet<T>`, `ContainsKey`, `Remove`, `Keys`, `Count`, `Merge`, `Matches`, `Clone`. |

Значениями свойств могут быть `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `string`, `Guid`, массивы `byte`, `int`, `float`, `string` или `object`, вложенный `PropertyTable`, типы Unity `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int` и `Vector3Int`, а также типы с атрибутом `[NetSerializable]`. Что может содержать тип `[NetSerializable]`, перечислено на странице [сообщения](messages.md). Любой другой тип при отправке вызывает исключение сериализации.

## Уведомления об изменениях

Кроме `OnChanged`, можно слушать все изменения через `Pulse.Events.OnRoomPropertiesChanged` и `OnPlayerPropertiesChanged` (или интерфейс `IRoomCallbacks`, см. [подключение](connecting.md#states-and-events)). Они передают только изменённые ключи в виде `PropertyTable`, а не всю таблицу. Ваши собственные записи вызывают эти события сразу; compare-and-set вызывает их после того, как сервер принял изменение.
