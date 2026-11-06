# Async

Каждый запрос, который выполняется не мгновенно, возвращает `NetOperation` (или `NetOperation<T>`, если есть результат). Один и тот же тип операции работает с `await`, с корутинами и с колбэками. `Awaitable` не нужен, поэтому код одинаково работает в Unity 2021.3 и в Unity 6.

```csharp
using System.Collections;
using NetPulse;
using UnityEngine;

public sealed class OperationStyles : MonoBehaviour
{
    private async void AsAwait()
    {
        var room = await Pulse.JoinOrCreateRoom("arena");
        Debug.Log($"Joined {room.Name}");
    }

    private IEnumerator AsCoroutine()
    {
        var operation = Pulse.JoinOrCreateRoom("arena");
        yield return operation;
        Debug.Log(operation.IsSuccess ? operation.Result.Name : operation.Error.ToString());
    }

    private void AsCallback()
    {
        Pulse.JoinOrCreateRoom("arena").Completed += operation => Debug.Log(operation.IsSuccess ? "ok" : operation.Error.ToString());
    }
}
```

## Объект операции

| Член | Значение |
|---|---|
| `IsDone` | Операция завершилась, успешно или нет |
| `IsSuccess` | Завершена, без ошибки и не отменена |
| `HasError`, `Error` | Ошибка. `Error` имеет тип `NetError` с полями `ReturnCode` и `Message`. |
| `IsCanceled` | `Cancel()` вызвали до завершения |
| `Result` | Значение `NetOperation<T>`, корректно после успеха |
| `Completed` | Событие, которое поднимается при завершении. Если операция уже завершена, новый подписчик вызывается сразу. |
| `Cancel()` | Прекращает ожидание, см. ниже |

`NetOperation` наследует `CustomYieldInstruction`, поэтому `yield return operation` ждёт операцию без лишнего кода. Продолжение после `await` и обработчики `Completed` выполняются в главном потоке, в обновлении NetPulse, так что объектами Unity можно пользоваться свободно.

## Ошибки

- `await` бросает `NetPulseException` для неудавшейся операции. В нём есть `ReturnCode` и сообщение. Коды сервера см. в разделе [коды и ключи](../reference/codes.md), коды клиента (`Timeout`, `Disconnected`, `InvalidState`) в разделе [подключение](connecting.md#operation-errors).
- В корутине или колбэке проверяйте `HasError` и `Error`. Ничего не бросается.
- Исключение, брошенное обработчиком `Completed`, попадает в лог и не влияет на остальные обработчики.
- Оборачивайте `await` в `try/catch` внутри метода `async void`. Иначе исключение попадёт в лог Unity без контекста.

```csharp
using NetPulse;
using UnityEngine;

public sealed class SafeJoin : MonoBehaviour
{
    private async void Start()
    {
        try
        {
            await Pulse.Connect();
            await Pulse.JoinRoom("arena");
        }
        catch (NetPulseException e)
        {
            Debug.LogWarning($"Failed with {e.ReturnCode}: {e.Message}");
        }
    }
}
```

## Отмена

`operation.Cancel()` сразу завершает операцию с признаком `IsCanceled`. Ожидание отменённой операции бросает `OperationCanceledException`. Для ожиданий, которые живут только на клиенте (`WaitForPlayersAsync` и `WaitUntilAsync`), `Cancel()` снимает ожидание. Для запросов, которые уже отправлены на сервер, таких как `Connect()`, `JoinRoom()` или вызовы владения, он лишь прекращает ваше ожидание: запрос всё равно доходит до сервера и там срабатывает.

```csharp
using System;
using NetPulse;
using UnityEngine;

public sealed class WaitForOpponents : MonoBehaviour
{
    private NetOperation _waiting;

    public async void Begin()
    {
        _waiting = Pulse.WaitForPlayersAsync(2);

        try
        {
            await _waiting;
            Debug.Log("Opponent found");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("Stopped waiting");
        }
    }

    public void Stop() => _waiting?.Cancel();
}
```

## Помощники ожидания

| Вызов | Завершается, когда |
|---|---|
| `Pulse.WaitForPlayersAsync(count)` | В комнате не меньше `count` игроков (неактивные считаются). Если это уже так, завершается сразу. Завершается ошибкой `InvalidState` вне комнаты или если вы вышли из комнаты во время ожидания. |
| `Pulse.WaitUntilAsync(Func<bool> condition)` | `condition()` вернула true. Проверяется сразу, а затем раз в кадр. Если условие бросает исключение, операция завершается ошибкой `InvalidState` с текстом исключения. |
| `networkObject.RequestOwnershipAsync(timeoutSeconds)` | `NetOperation<bool>`, см. [владение](ownership.md) |
| `networkObject.TransferOwnershipAsync(player, timeoutSeconds)` | `NetOperation<bool>` |

```csharp
using NetPulse;
using UnityEngine;

public sealed class StartWhenReady : MonoBehaviour
{
    private bool _ready;

    public void MarkReady() => _ready = true;

    private async void Start()
    {
        await Pulse.WaitForPlayersAsync(2);
        await Pulse.WaitUntilAsync(() => _ready);
        Debug.Log("Everyone is here and ready");
    }
}
```

## Собственные операции

`NetOperation.Succeeded()`, `NetOperation.Failed(code, message)`, `NetOperation<T>.Succeeded(result)` и `NetOperation<T>.Failed(code, message)` создают уже завершённые операции. Они пригодятся, когда ваш метод должен вернуть объект того же вида, что и вызовы NetPulse, например в тестах или при досрочном отказе из-за невыполненного условия.
