# Таймеры, HTTP и фоновая работа

Хуки срабатывают, когда игрок что-то делает. Но плагинам нужна и работа по собственному расписанию, и работа вне очереди комнаты. Для этого у хоста есть четыре инструмента: таймеры, `Enqueue`, `HttpAsync` и `RunInBackground`.

## Таймеры

```csharp
IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Action callback);
IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Func<CancellationToken, ValueTask> callback);
```

- Колбэк впервые срабатывает через `dueTime`. Нулевой или отрицательный `dueTime` запускает его сразу.
- Если `period` положительный, колбэк повторяется с этим интервалом. Нулевой, отрицательный период или `Timeout.InfiniteTimeSpan` делают таймер одноразовым.
- Колбэки выполняются на файбере комнаты, поэтому могут работать с состоянием комнаты без блокировок.
- Тик не начинается, пока выполняется предыдущий колбэк. Если колбэк работает дольше периода, пропущенные тики не накапливаются.
- Перегрузка с `Func` позволяет колбэку использовать `await`. Пока он ждёт, комната продолжает обрабатывать сообщения: колбэк таймера не является элементом очереди, поэтому комнату не задерживает, но между его `await` может выполняться другой код.
- Исключение в колбэке попадает в `OnUnhandledExceptionAsync`. Таймер продолжает работать.
- Чтобы остановить таймер, вызовите `Dispose()` у возвращённого объекта. Комната сама останавливает все свои таймеры при закрытии.

Обратный отсчёт, который начинается, когда входит первый игрок:

```csharp title="CountdownPlugin.cs"
using NetPulse.Plugins;

namespace TimerExamples;

public sealed class CountdownPlugin : RoomPlugin
{
    public const byte CountdownEvent = 80;
    private const int Seconds = 10;

    private IDisposable? _timer;
    private int _remaining = Seconds;

    public CountdownPlugin(IPluginHost host)
        : base(host)
    {
    }

    public override ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct)
    {
        _timer ??= Host.CreateTimer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TickAsync);
        info.Continue();
        return default;
    }

    public override ValueTask OnCloseRoomAsync(ICloseRoomCallInfo info, CancellationToken ct)
    {
        _timer?.Dispose();
        info.Continue();
        return default;
    }

    private ValueTask TickAsync(CancellationToken ct)
    {
        _remaining--;
        Host.BroadcastEvent(CountdownEvent, _remaining);

        if (_remaining <= 0)
        {
            _timer?.Dispose();
            _timer = null;
        }

        return default;
    }
}
```

## Enqueue

```csharp
void Enqueue(Action action);
```

`Enqueue` ставит действие в конец очереди комнаты. Оно выполняется на файбере после того, как закончится элемент, выполняемый сейчас. Подходит, чтобы:

- вернуться на файбер из потока, который вы запустили сами;
- сделать что-то после завершения текущего действия хука, например отправить сообщение после ответа на вход (см. [Написание плагина](writing-a-plugin.md));
- разбить длинную работу на шаги, чтобы между ними могли выполняться другие сообщения.

Метод можно вызывать из любого потока. Исключение в действии попадает в `OnUnhandledExceptionAsync`. Если комната уже закрылась, вызов ничего не делает.

## HTTP

```csharp
Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct = default);
```

`HttpAsync` отправляет запрос через `IHttpClientFactory` сервера, поэтому соединения объединяются в пул и используются совместно. У клиента фиксированный тайм-аут 15 секунд. Хук, который его ожидает, ограничен `HookTimeoutMs`, поэтому передавайте `ct` хука, чтобы запрос прервался вместе с хуком.

Если ждать вызов внутри хука, комната ждёт вместе с вами, а продолжаете вы на файбере. В разделе [Хуки](hooks.md) есть два примера: проверка блокировки и выгрузка результата. Метод может бросить `HttpRequestException` или `TaskCanceledException`; решите, должен ли неудачный вызов отклонять действие или пропускать его.

Безопасно вызывать его и вне файбера, например из `RunInBackground`.

## Фоновая работа

```csharp
void RunInBackground(Func<CancellationToken, Task> work);
```

`RunInBackground` запускает работу в пуле потоков, вне файбера комнаты. Комната её не ждёт. Применяйте его, когда результат не влияет на исход вызова: аналитика, вебхуки, сохранение статистики.

- Токен отменяется при закрытии комнаты. `OperationCanceledException` после этого игнорируется.
- Любое другое исключение записывается в лог как `Background work in room X failed`. В `OnUnhandledExceptionAsync` оно не попадает.
- Фоновый код работает вне файбера. Не читайте из него `Actors`, `RoomProperties` и другое состояние комнаты. Прочитайте нужное заранее на файбере и захватите в замыкание.
- Чтобы вернуть результат, вызовите `Host.Enqueue` либо один из методов `BroadcastEvent`, `SetRoomProperties`, `SetPlayerProperties`, `KickActor` или `CloseRoom`. Они сами передают работу файберу.

Плагин, который отправляет каждое сообщение чата на вебхук, не задерживая чат, и сообщает комнате о сбое:

```csharp title="WebhookPlugin.cs"
using System.Net.Http.Json;
using NetPulse.Plugins;

namespace TimerExamples;

public sealed class WebhookPlugin : RoomPlugin
{
    private const byte ChatEvent = 20;
    private const byte NoticeEvent = 71;

    private readonly string _webhookUrl;

    public WebhookPlugin(IPluginHost host, string webhookUrl)
        : base(host) => _webhookUrl = webhookUrl;

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        if (info.Request.EventCode == ChatEvent && info.Request.Data is string text)
        {
            var room = Host.RoomName;
            var author = Host.GetActor(info.ActorId)?.NickName ?? "unknown";
            Host.RunInBackground(token => PostAsync(room, author, text, token));
        }

        info.Continue();
        return default;
    }

    private async Task PostAsync(string room, string author, string text, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _webhookUrl)
        {
            Content = JsonContent.Create(new { room, author, text })
        };

        using var response = await Host.HttpAsync(request, token);

        if (!response.IsSuccessStatusCode)
        {
            Host.BroadcastEvent(NoticeEvent, "The chat log is unavailable");
        }
    }
}
```

Если `PostAsync` бросит исключение, сервер запишет его в лог, а комната продолжит работу.

## Какой инструмент выбрать

| Нужно | Используйте |
|---|---|
| Делать что-то каждые N секунд или один раз позже | `CreateTimer` |
| Дождаться HTTP-ответа, прежде чем решить судьбу хука | `await Host.HttpAsync(request, ct)` в хуке |
| Запустить медленную работу и не ждать | `RunInBackground` |
| Вернуться в комнату из другого кода или выполниться после текущего элемента | `Enqueue` |
