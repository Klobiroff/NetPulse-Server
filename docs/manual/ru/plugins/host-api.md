# API хоста

`IPluginHost` позволяет плагину читать свою комнату и менять её. У каждой комнаты свой хост, а `RoomPlugin` показывает его как `Host`. Фабрика получает тот же хост в `CreateAsync`.

## Чтение состояния комнаты

| Член | Тип | Описание |
|---|---|---|
| `RoomName` | `string` | Имя комнаты. |
| `Actors` | `IReadOnlyList<IActor>` | Снимок всех актёров комнаты, включая неактивных, в порядке входа. |
| `MasterClientId` | `int` | Идентификатор мастер-клиента или 0, если активных игроков нет. Мастером становится активный игрок, находящийся в комнате дольше всех. |
| `RoomProperties` | `PropertyTable` | Живые свойства комнаты. Читать их можно свободно, а менять нужно через `SetRoomProperties`, чтобы клиенты узнали об изменении. |
| `ServerTimeMs` | `long` | Миллисекунды с момента запуска сервера. Это те же часы, которые клиенты получают как серверное время. |
| `Logger` | `ILogger` | Логгер с категорией `NetPulse.Room.<имя комнаты>`. |
| `GetActor(actorId)` | `IActor?` | Один актёр или `null`. |
| `GetNetworkObject(viewId)` | `INetworkObject?` | Один зарегистрированный сетевой объект или `null`. |
| `NetworkObjects` | `IReadOnlyCollection<INetworkObject>` | Снимок всех зарегистрированных сетевых объектов. |

У `IActor` есть `ActorId`, `UserId`, `NickName`, `Properties`, `IsInactive` и `IsMasterClient`. Неактивный актёр: игрок, который отключился и может вернуться; события он не получает.

У `INetworkObject` есть `ViewId`, `CreatorActorId`, `OwnerActorId`, `OwnershipMode`, `IsSceneObject` и `IsPersistent`. Для сценового объекта, который никто не забрал, `OwnerActorId` показывает текущего мастера.

!!! warning "Читайте состояние комнаты только на файбере комнаты"
    `Actors`, `GetActor`, `RoomProperties`, `NetworkObjects` и `GetNetworkObject` читают состояние комнаты напрямую. Используйте их в хуках, колбэках таймеров, действиях `Enqueue` и после `await` в хуке. Не обращайтесь к ним из `RunInBackground` и из потока, который запустили сами. `Logger`, `ServerTimeMs`, `RoomName` и `HttpAsync` безопасны везде.

## Действия над комнатой

| Член | Описание |
|---|---|
| `BroadcastEvent(code, data, receivers, targets, caching, delivery, senderActorId)` | Отправить событие игрокам. |
| `SetRoomProperties(props, broadcast)` | Влить свойства в комнату. |
| `SetPlayerProperties(actorId, props, broadcast)` | Влить свойства в игрока. |
| `KickActor(actorId, reason)` | Убрать игрока из комнаты. |
| `CloseRoom()` | Закрыть комнату. |
| `CreateTimer(...)`, `Enqueue(...)`, `HttpAsync(...)`, `RunInBackground(...)` | См. [Таймеры, HTTP и фоновая работа](timers-http-background.md). |

Эти методы можно вызывать из любого потока. Вызванные с файбера комнаты, они действуют сразу. Вызванные из другого места, они ставятся в очередь и выполняются на файбере.

### BroadcastEvent

```csharp
void BroadcastEvent(
    byte code,
    object? data,
    ReceiverGroup receivers = ReceiverGroup.All,
    int[]? targets = null,
    CachingOption caching = CachingOption.DoNotCache,
    DeliveryMode delivery = DeliveryMode.ReliableOrdered,
    int senderActorId = 0);
```

- `code` задаёт код события. Для своих событий используйте от 0 до 199, тот же диапазон, что разрешён клиентам.
- В `data` передаётся любое значение, которое поддерживает сетевой формат: числа, строки, массивы, `PropertyTable` или типы, зарегистрированные в `NetTypeRegistry`. См. [Сетевой протокол](../reference/protocol.md#value-types).
- `receivers` принимает `Others`, `All`, `MasterClient` или `Targets`. `Targets` отправляет актёрам из списка `targets`. Неактивные актёры события не получают.
- `caching` управляет кэшем событий для опоздавших игроков. Событие, закэшированное с `senderActorId`, равным 0, принадлежит серверу и остаётся, пока его не удалят, даже после ухода игроков.
- `senderActorId` определяет, кого клиенты видят как отправителя. 0 означает сервер. При отправителе 0 `Others` доходит до всех.

События из плагина `OnRaiseEventAsync` не проходят. Клиенты получают их как любые другие события.

### SetRoomProperties и SetPlayerProperties

Оба метода вливают таблицу в существующие свойства; значение `null` удаляет ключ. Если `broadcast` истинно (по умолчанию), все игроки получают событие об изменении свойств; идентификатор отправителя в нём равен 0 (сервер).

Эти вызовы не запускают `OnBeforeSetPropertiesAsync` и не подчиняются правилу зарезервированных ключей, так что плагин может сам менять `_isOpen`, `_isVisible`, `_maxPlayers` и `_scene`. После изменения комнаты список лобби обновляется. `SetPlayerProperties` игнорирует неизвестный идентификатор актёра.

### KickActor и CloseRoom

`KickActor` сообщает игроку событием `ErrorInfo` (`Forbidden` и вашу причину), затем убирает его из комнаты. `OnLeaveAsync` вызывается с указанной вами причиной и `IsInactive`, равным false. Игрок остаётся подключённым к серверу и может войти в другую комнату.

`CloseRoom` отправляет каждому игроку событие `ErrorInfo` с `RoomClosed`, затем закрывает комнату. `OnBeforeCloseRoomAsync` пропускается, а `OnCloseRoomAsync` вызывается с причиной `closed by plugin`.

## Пример

Плагин, который позволяет мастер-клиенту выполнять две команды через событие 70: `kick <actorId>` и `close`. Кроме того, он записывает в свойства каждого вошедшего игрока время входа.

```csharp title="AdminCommandsPlugin.cs"
using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;

namespace HostExamples;

public sealed class AdminCommandsPlugin : RoomPlugin
{
    public const byte CommandEvent = 70;
    public const byte NoticeEvent = 71;

    public AdminCommandsPlugin(IPluginHost host)
        : base(host)
    {
    }

    public override ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct)
    {
        Host.SetPlayerProperties(info.Actor.ActorId, new PropertyTable { ["joinedAtMs"] = Host.ServerTimeMs });
        info.Continue();
        return default;
    }

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        if (info.Request.EventCode != CommandEvent)
        {
            info.Continue();
            return default;
        }

        if (info.ActorId != Host.MasterClientId)
        {
            info.Fail("Only the master client can run commands");
            return default;
        }

        Run(info.Request.Data as string ?? string.Empty);
        info.Cancel();
        return default;
    }

    private void Run(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 2 && parts[0] == "kick" && int.TryParse(parts[1], out var actorId) && Host.GetActor(actorId) is { } target)
        {
            Host.BroadcastEvent(NoticeEvent, $"{target.NickName} was kicked");
            Host.KickActor(actorId, "Kicked by the master client");
            return;
        }

        if (parts.Length == 1 && parts[0] == "close")
        {
            Host.Logger.LogInformation("Room {Room} closed by command", Host.RoomName);
            Host.CloseRoom();
            return;
        }

        Host.Logger.LogWarning("Unknown command '{Command}'", command);
    }
}
```

Событие с командой отменяется, поэтому остальные игроки его не получают, а отправитель получает `Ok`, если запрашивал ответ.
