# Timers, HTTP and background work

Hooks run when a player does something. Plugins also need work that happens on its own schedule or outside the room's queue. The host offers four tools for it: timers, `Enqueue`, `HttpAsync` and `RunInBackground`.

## Timers

```csharp
IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Action callback);
IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Func<CancellationToken, ValueTask> callback);
```

- The callback first runs after `dueTime`. A zero or negative `dueTime` runs it right away.
- If `period` is positive, the callback repeats at that interval. A zero, negative or `Timeout.InfiniteTimeSpan` period makes the timer fire once.
- Callbacks run on the room fiber, so they can touch room state without locks.
- A tick never starts while the previous callback is still running. If a callback takes longer than the period, the missed ticks do not pile up.
- The `Func` overload lets the callback `await`. While it waits, the room keeps handling messages: a timer callback is not a queue item, so it never holds up the room, but other code can run between its awaits.
- An exception in a callback goes to `OnUnhandledExceptionAsync`. The timer keeps running.
- Call `Dispose()` on the returned object to stop the timer. The room also stops all its timers when it closes.

A countdown that starts when the first player joins:

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

`Enqueue` puts an action at the end of the room's queue. It runs after the item that is running now has finished, on the room fiber. Use it to:

- come back to the fiber from a thread you started;
- do something after the current hook's action completes, such as sending a message after a join response (see [Writing a plugin](writing-a-plugin.md));
- split a long job into steps so that other messages can run in between.

It is safe to call from any thread. An exception in the action goes to `OnUnhandledExceptionAsync`. If the room has already closed, the call does nothing.

## HTTP

```csharp
Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct = default);
```

`HttpAsync` sends a request through the server's `IHttpClientFactory`, so connections are pooled and shared. The client has a fixed timeout of 15 seconds. The `HookTimeoutMs` limit applies to a hook that awaits it, so pass the hook's `ct` to cut the request off when the hook times out.

Awaited inside a hook, the call keeps the room waiting and you resume on the fiber. [Hooks](hooks.md) has two examples, a ban check and a result upload. The method can throw `HttpRequestException` or `TaskCanceledException`; decide whether a failed call should reject the action or let it through.

It is also safe to call outside the fiber, for example from `RunInBackground`.

## Background work

```csharp
void RunInBackground(Func<CancellationToken, Task> work);
```

`RunInBackground` starts the work on the thread pool, off the room fiber. The room does not wait for it. Use it when the result does not change the outcome of a call: analytics, webhooks, saving a statistic.

- The token is cancelled when the room closes. An `OperationCanceledException` after that is ignored.
- Any other exception is logged as `Background work in room X failed`. It does not reach `OnUnhandledExceptionAsync`.
- Background code runs outside the fiber. Do not read `Actors`, `RoomProperties` or other room state from it. Read what you need first, on the fiber, and capture it in the closure.
- To report a result back, call `Host.Enqueue`, or one of `BroadcastEvent`, `SetRoomProperties`, `SetPlayerProperties`, `KickActor` or `CloseRoom`. They hand the work to the fiber for you.

A plugin that posts each chat message to a webhook without delaying the chat, and announces a failure to the room:

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

If `PostAsync` throws, the server logs it and the room carries on.

## Choosing a tool

| You want to | Use |
|---|---|
| Do something every N seconds, or once later | `CreateTimer` |
| Wait for an HTTP answer before deciding a hook | `await Host.HttpAsync(request, ct)` in the hook |
| Fire and forget slow work | `RunInBackground` |
| Return to the room from other code, or run after the current item | `Enqueue` |
