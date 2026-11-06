# Хуки

Хуки представляют собой методы `RoomPlugin`, которые комната вызывает в определённые моменты. Переопределяйте нужные: базовый класс по умолчанию пропускает любой вызов.

## Как работает хук

Каждый хук представляет собой метод с `ValueTask` одной и той же формы:

```csharp
public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
```

`info` описывает вызов и несёт решение. `ct` срабатывает, когда у хука заканчивается время. До возврата хук должен принять решение:

| Метод | Значение |
|---|---|
| `info.Continue()` | Пропустить действие. |
| `info.Fail(message)` | Отклонить действие. Клиент получит `PluginReportedError` (-20) с вашим сообщением. |
| `info.Cancel()` | Тихо отбросить действие. Клиент получит `Ok`. Есть только у хуков, `info` которых наследует `ICancellableCallInfo`. |

У каждого `info` также есть `ActorId` (актёр, вызвавший действие, либо 0, если такого нет) и `IsProcessed`, которое становится истинным после принятия решения.

Решение можно принять до `await` или после. Хук может быть `async`, а после каждого `await` код снова работает на файбере комнаты, см. [комната делает одно дело за раз](overview.md#the-room-runs-one-thing-at-a-time).

## Все хуки

| Хук | Когда вызывается | Что делает `Fail` | `Cancel` |
|---|---|---|---|
| `OnCreateRoomAsync` | При создании комнаты, до входа первого игрока. | Комната закрывается, создатель получает -20. | Нет |
| `OnBeforeJoinAsync` | Игрок хочет войти, комната открыта и не заполнена. | Вход отклоняется с -20. | Нет |
| `OnJoinAsync` | Игрок добавлен в комнату, ответ на вход ещё не отправлен. | Вход отменяется, игрок получает -20. | Нет |
| `OnLeaveAsync` | Игрок выходит, отключается или выгнан, до удаления. | Только запись в лог, выход продолжается. | Нет |
| `OnRaiseEventAsync` | Клиент отправил событие, после собственных проверок сервера, до рассылки. | Событие не рассылается, отправитель получает -20. | Не рассылается, отправитель получает `Ok` |
| `OnBeforeSetPropertiesAsync` | Клиент меняет свойства комнаты или игрока, после проверок прав. | Ничего не применяется, отправитель получает -20. | Ничего не применяется, отправитель получает `Ok` |
| `OnSetPropertiesAsync` | Свойства применены, до рассылки изменения. | Только запись в лог. | Нет |
| `OnBeforeTransferOwnershipAsync` | Владение сетевым объектом вот-вот изменится или запрошено. | Изменение отклоняется с -20. | Ничего не меняется, отправитель получает `Ok` |
| `OnBeforeCloseRoomAsync` | Комната опустела или сервер останавливается. | Комната остаётся открытой, если закрытие не принудительное. | То же, что `Fail` |
| `OnCloseRoomAsync` | Комната закрывается. | Не действует. | Нет |
| `OnUnhandledExceptionAsync` | Код плагина бросил исключение. Это не хук-решение. | | |

## Подробно о хуках

### OnCreateRoomAsync

Вызывается один раз, после того как фабрика создала плагин, и до входа создателя. В `info` есть `Options` (`RoomOptions`, присланные клиентом), `UserId`, `NickName` и `PlayerProperties`. Используйте его, чтобы отклонять настройки комнаты, которые не подходят вашей игре.

```csharp title="RoomLimitsPlugin.cs"
using NetPulse.Plugins;

namespace HookExamples;

public sealed class RoomLimitsPlugin : RoomPlugin
{
    private const int MaxPlayersLimit = 16;

    public RoomLimitsPlugin(IPluginHost host)
        : base(host)
    {
    }

    public override ValueTask OnCreateRoomAsync(ICreateRoomCallInfo info, CancellationToken ct)
    {
        if (info.Options.MaxPlayers is 0 or > MaxPlayersLimit)
        {
            info.Fail($"MaxPlayers must be between 1 and {MaxPlayersLimit}");
            return default;
        }

        info.Continue();
        return default;
    }
}
```

### OnBeforeJoinAsync и OnJoinAsync

Порядок входа такой: сервер проверяет, что комната открыта и не заполнена, затем вызывает `OnBeforeJoinAsync`, добавляет игрока, вызывает `OnJoinAsync`, отправляет ответ на вход, объявляет остальным `PlayerJoined` и воспроизводит новичку кэш событий.

В `OnBeforeJoinAsync` есть `UserId`, `NickName`, `IsRejoin` и `PlayerProperties`. Для нового игрока таблица `PlayerProperties`, которую вы оставите, станет его начальными свойствами, поэтому ключи можно добавлять и убирать. `ActorId` при повторном входе равен существующему идентификатору актёра, иначе 0. Это подходящее место для проверок, требующих ввода-вывода, например списка блокировок:

```csharp title="BanCheckPlugin.cs"
using Microsoft.Extensions.Logging;
using NetPulse.Plugins;

namespace HookExamples;

public sealed class BanCheckPlugin : RoomPlugin
{
    private readonly string _banServiceUrl;

    public BanCheckPlugin(IPluginHost host, string banServiceUrl)
        : base(host) => _banServiceUrl = banServiceUrl;

    public override async ValueTask OnBeforeJoinAsync(IBeforeJoinCallInfo info, CancellationToken ct)
    {
        var url = $"{_banServiceUrl}?user={Uri.EscapeDataString(info.UserId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        try
        {
            using var response = await Host.HttpAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                info.Fail("You are banned from this room");
                return;
            }
        }
        catch (HttpRequestException ex)
        {
            Host.Logger.LogWarning(ex, "Ban service is unreachable, letting {User} in", info.UserId);
        }

        info.Continue();
    }
}
```

В `OnJoinAsync` есть новый `Actor` (`IActor`) и `IsRejoin`. Игрок уже находится в `Host.Actors`, но его клиент ещё не получил ответ на вход. Всё, что вы отправите этому клиенту из хука, придёт раньше ответа. Чтобы отправить сообщение после завершения входа, используйте `Host.Enqueue`, как в разделе [Написание плагина](writing-a-plugin.md). Если `OnJoinAsync` завершится ошибкой, сервер откатит вход.

### OnLeaveAsync

Вызывается при любом способе ухода игрока: запрос `LeaveRoom` (причина `left`), отключение (причина `disconnect`) или исключение из комнаты (причина, которую вы передали в `Host.KickActor`). `info.Actor` ещё находится в комнате. `IsInactive` истинно, если игрок остаётся неактивным актёром, чтобы вернуться: так бывает, когда у комнаты задан `PlayerTtlMs`, а игрок отключился или попросил стать неактивным.

Хук вызывается один раз на уход. Когда время жизни неактивного игрока истекает позже, игрок удаляется без повторного `OnLeaveAsync`. Неудачный `OnLeaveAsync` не может оставить игрока в комнате.

Когда закрывается вся комната, игроки удаляются без `OnLeaveAsync`. Для работы в конце жизни комнаты используйте `OnCloseRoomAsync`.

### OnRaiseEventAsync

Вызывается для каждого события, которое отправляет клиент: ваши события (коды от 0 до 199) и объектные события: создание, удаление, RPC, синхронизация состояния, сообщение и мастерская sync-переменная. К этому моменту сервер уже отклонил зарезервированные коды и нарушения владения, см. [Безопасность](../server/security.md). `info.Request` имеет тип `RaiseEventRequest` и содержит поля `EventCode`, `Data`, `Receivers`, `TargetActors`, `InterestGroup`, `Caching`, `Delivery` и `ViewId`.

Запрос можно изменять. Если вы продолжаете, событие уходит в том виде, какой имеет к возврату из хука, так что можно переписать `Data`, поменять `Receivers` или `Caching`. Если `Data` пришла как `OpaqueValue` (пользовательский тип, неизвестный серверу), её можно прочитать через `NetReader`.

```csharp title="ChatRulesPlugin.cs"
using NetPulse.Plugins;

namespace HookExamples;

public sealed class ChatRulesPlugin : RoomPlugin
{
    private const byte ChatEvent = 20;
    private const int MaxLength = 200;

    private readonly HashSet<int> _muted = new();

    public ChatRulesPlugin(IPluginHost host)
        : base(host)
    {
    }

    public void Mute(int actorId) => _muted.Add(actorId);

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        if (info.Request.EventCode != ChatEvent)
        {
            info.Continue();
            return default;
        }

        if (_muted.Contains(info.ActorId))
        {
            info.Cancel();
            return default;
        }

        if (info.Request.Data is not string text || text.Length > MaxLength)
        {
            info.Fail("Chat messages must be text of up to 200 characters");
            return default;
        }

        info.Continue();
        return default;
    }
}
```

Этот хук стоит на пути каждого RPC и каждой синхронизации состояния каждого игрока, поэтому типичный случай должен быть быстрым: сначала проверьте код события и сразу вызывайте `Continue()` для всего, что вас не интересует.

### OnBeforeSetPropertiesAsync и OnSetPropertiesAsync

`OnBeforeSetPropertiesAsync` вызывается после проверок прав на сервере (зарезервированные ключи, чужие свойства) и до применения свойств. `TargetActorId` равен 0 для свойств комнаты и идентификатору актёра для свойств игрока. `Properties` содержит изменение (значение `null` удаляет ключ), а `ExpectedProperties` хранит условие сравнения и замены (CAS), приложенное клиентом, либо `null`. Сервер проверяет условие после хука, поэтому хук, который продолжает вызов, не отменяет проверку.

Вызовите `Fail`, чтобы отклонить с сообщением, или `Cancel`, чтобы проигнорировать изменение, не сообщая клиенту об ошибке. Пример есть на странице [Безопасность](../server/security.md#reserved-properties).

`OnSetPropertiesAsync` вызывается после применения свойств и до их рассылки. В нём есть `TargetActorId` и `Properties`. Отменить он ничего не может: используйте его для реакции, например чтобы пересчитать производное значение.

### OnBeforeTransferOwnershipAsync

Вызывается, когда владение сетевым объектом вот-вот изменится и когда игрок запрашивает объект в режиме `Request`. В `info` есть `ViewId`, `NewOwnerActorId`, `CurrentOwnerActorId`, `OwnershipMode` и `IsRequest`. `IsRequest` истинно на первом шаге для объекта в режиме `Request`, когда сервер собирается переслать запрос владельцу. Последующая передача, когда владелец одобряет, снова вызывает хук с `IsRequest`, равным false.

```csharp title="LockedObjectsPlugin.cs"
using NetPulse.Plugins;

namespace HookExamples;

public sealed class LockedObjectsPlugin : RoomPlugin
{
    private readonly HashSet<int> _locked = new();

    public LockedObjectsPlugin(IPluginHost host)
        : base(host)
    {
    }

    public void Lock(int viewId) => _locked.Add(viewId);

    public void Unlock(int viewId) => _locked.Remove(viewId);

    public override ValueTask OnBeforeTransferOwnershipAsync(IBeforeTransferOwnershipCallInfo info, CancellationToken ct)
    {
        if (_locked.Contains(info.ViewId))
        {
            info.Fail($"Object {info.ViewId} is locked");
            return default;
        }

        info.Continue();
        return default;
    }
}
```

### OnBeforeCloseRoomAsync и OnCloseRoomAsync

`OnBeforeCloseRoomAsync` вызывается, когда вышел последний игрок (причина `empty`, после `EmptyRoomTtlMs`, если клиент его задал), и при остановке сервера (причина `server shutdown`). `Fail` или `Cancel` оставляют опустевшую комнату живой. В следующий раз сервер спросит снова, когда комната опустеет. Остановка сервера игнорирует ответ и закрывает комнату в любом случае. `Host.CloseRoom()` этот хук тоже пропускает.

`OnCloseRoomAsync` вызывается всегда, когда комната закрывается. `Reason` принимает одно из значений: `empty`, `server shutdown`, `closed by plugin` или причина сбоя создания (`plugin initialization failed`, `create rejected by plugin`, `create failed`).

К началу `OnCloseRoomAsync` комната уже остановила свои таймеры и отменила токен `RunInBackground`, игроков в ней нет, а рассылка никого не достигнет. Остаётся время сохранить результаты. Дожидайтесь работы прямо в хуке; действует `HookTimeoutMs`.

```csharp title="MatchResultPlugin.cs"
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using NetPulse.Plugins;

namespace HookExamples;

public sealed class MatchResultPlugin : RoomPlugin
{
    private readonly string _resultsUrl;

    public MatchResultPlugin(IPluginHost host, string resultsUrl)
        : base(host) => _resultsUrl = resultsUrl;

    public override async ValueTask OnCloseRoomAsync(ICloseRoomCallInfo info, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _resultsUrl)
        {
            Content = JsonContent.Create(new { room = Host.RoomName, reason = info.Reason, closedAtMs = Host.ServerTimeMs })
        };

        try
        {
            using var response = await Host.HttpAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Host.Logger.LogWarning(ex, "Could not save the result of room {Room}", Host.RoomName);
        }

        info.Continue();
    }
}
```

### OnUnhandledExceptionAsync

```csharp
public virtual ValueTask OnUnhandledExceptionAsync(Exception ex, ICallInfo? info)
```

Комната вызывает его, когда код плагина бросает исключение: в хуке, в колбэке таймера или в действии, переданном в `Host.Enqueue`. `info` указывает на вызов, завершившийся ошибкой, либо равен `null`, если исключение пришло не из хука. Реализация по умолчанию записывает исключение в лог. Переопределите метод, чтобы сообщать об ошибках в другое место. Комната продолжает работать, а упавший вызов получает `Fail`.

Если сам этот метод бросит исключение, комната запишет это в лог и продолжит работу.

## Ошибки и тайм-ауты {#errors-and-timeouts}

- Хук, который бросает исключение или чей `ValueTask` завершается ошибкой, попадает в `OnUnhandledExceptionAsync`, увеличивает счётчик ошибок плагинов, который показывает команда `stats`, и завершает вызов с `Fail` и сообщением `Plugin error`.
- Хук, вернувшийся без решения, записывается в лог как ошибка и завершает вызов с `Plugin did not respond`.
- На каждый хук отводится `NetPulse:Plugins:HookTimeoutMs` (по умолчанию 5 секунд), так же как и на фабрику плагина. Когда время выходит, вызов завершается с `Plugin hook timed out`, сервер пишет предупреждение и отменяет `ct`. Комната идёт дальше. Запоздавший `Continue()` игнорируется с предупреждением. Передавайте `ct` в каждый `await`, который его принимает, иначе заброшенный хук продолжит работать в фоне.
- Повторный вызов `Continue`, `Fail` или `Cancel` игнорируется с предупреждением. Побеждает первое решение.
- Исключения, возникшие после `await`, перехватываются так же, как и возникшие до него.

Тайм-ауты пишутся в лог, но `OnUnhandledExceptionAsync` не вызывают.
