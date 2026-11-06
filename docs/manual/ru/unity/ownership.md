# Владение

У каждого сетевого объекта один владелец, и только владелец отправляет состояние объекта. Реестр владельцев хранит сервер и проверяет каждое изменение, поэтому клиент никогда не решает сам, что объект теперь его. `IsMine` и `Owner` меняются только тогда, когда приходит событие владения от сервера, в том числе для запросов, которые вы отправили сами.

## Режимы

Режим задаётся полем `Ownership Mode` у `NetworkObject` (в коде `OwnershipMode`). Он уходит на сервер вместе с созданием объекта.

| Режим | Кто может стать владельцем |
|---|---|
| `Fixed` | Никто. Объект остаётся у создателя. `RequestOwnership` и `TransferOwnership` отклоняются. |
| `Takeover` | Любой. `RequestOwnership()` забирает объект сразу. Владелец или мастер также могут передать его игроку через `TransferOwnership(player)`. |
| `Request` | `RequestOwnership()` спрашивает текущего владельца, который может одобрить. Назначить через `TransferOwnership(player)` могут только владелец или мастер. |

Мастер может переназначить любой объект, кроме `Fixed`.

## Запрос и одобрение

В режиме `Request` владелец получает запрос как `OnOwnershipRequest(Player requester)`. Ответить он может вызовом `ApproveOwnership(requester)`. Отказ означает бездействие: запрос истекает на сервере (по умолчанию через 5 секунд, это серверная настройка), а запрашивающему ничего не сообщается. У одного запрашивающего может быть только один ожидающий запрос на объект.

```csharp
using NetPulse;
using UnityEngine;

public sealed class Crate : NetworkBehaviour
{
    public override void OnOwnershipRequest(Player requester)
    {
        if (requester.GetProperty("team", 0) == Owner.GetProperty("team", 0))
        {
            NetworkObject.ApproveOwnership(requester);
        }
    }

    public override void OnOwnerChanged(Player previous, Player current) => Debug.Log($"Now owned by {current?.NickName}");

    public void Grab() => NetworkObject.RequestOwnership();
}
```

`OnOwnershipRequest` вызывается только на клиенте владельца. Тот же запрос поднимается и как `Pulse.Events.OnOwnershipRequest(networkObject, requester)`, и через `IOwnershipCallbacks` ([подключение](connecting.md#states-and-events)). `ApproveOwnership` работает только для текущего владельца; остальные получают предупреждение.

## Ожидание результата

`RequestOwnership()` и `TransferOwnership()` возвращаются сразу и об исходе не сообщают. Асинхронные версии сообщают:

```csharp
using NetPulse;
using UnityEngine;

public sealed class Pickup : MonoBehaviour
{
    [SerializeField] private NetworkObject _target;

    public async void TryGrab()
    {
        var granted = await _target.RequestOwnershipAsync(3f);
        Debug.Log(granted ? "Got it" : "Refused or timed out");
    }

    public async void Give(Player player)
    {
        var moved = await _target.TransferOwnershipAsync(player);
        Debug.Log(moved ? "Handed over" : "Not handed over");
    }
}
```

| Вызов | Результат `true`, когда | Результат `false`, когда |
|---|---|---|
| `RequestOwnershipAsync(timeoutSeconds = 5)` | Вы владелец (уже или после подтверждения сервера) | Режим `Fixed`, вы не в комнате, сервер отказал или время вышло |
| `TransferOwnershipAsync(player, timeoutSeconds = 5)` | Игрок стал владельцем (уже или после подтверждения) | Те же причины |

Операции возвращают `NetOperation<bool>` и при отказе исключений не бросают. См. [async](async.md).

## Что происходит при смене владельца

Когда сервер объявляет смену, каждое поведение `NetworkBehaviour` на объекте получает `OnOwnerChanged(previous, current)`, затем `OnOwnershipGained()` или `OnOwnershipLost()`. После обработки всех поведений поднимаются `NetworkObject.OwnershipChanged` и `Pulse.Events.OnOwnershipChanged`. Новый владелец начинает отправлять полное состояние объекта. Компоненты подстраиваются: например, `NetworkRigidbody` делает тело кинематическим у не-владельцев ([компоненты](components.md)). Точный порядок вызовов описан на странице [жизненный цикл](lifecycle.md).

## Отказы

Когда сервер отклоняет операцию владения (`Forbidden`, `ObjectNotFound`), клиент пишет предупреждение и поднимает `Pulse.Events.OnOwnershipFailed(networkObject, returnCode, message)`. Локальное состояние остаётся прежним, а ожидающий асинхронный вызов завершается значением `false`. Сервер также отклоняет обновления состояния, создание и уничтожение объектов от неверного отправителя, и клиент пишет об этом не чаще одного предупреждения в секунду.

Серверный плагин может запретить или изменить передачу. См. [хуки](../plugins/hooks.md). Запрещённый запрос выглядит как запрос, который так и не одобрили.

## Сценовые объекты и уходящие игроки

- Сценовые объекты принадлежат мастеру (`OwnerActorId` равное 0 означает «мастер»). Мастер регистрирует их режимы на сервере после входа и после того, как становится мастером, так что сценовый объект `Takeover` может забрать любой игрок.
- Когда игрок выходит, сценовые объекты, которые он забрал, возвращаются мастеру.
- Остальные объекты, которыми игрок владел в этот момент, уничтожаются у всех клиентов. Объекты, которые он уже передал другому, остаются. Игрок, который лишь неактивен, сохраняет свои объекты.
- Уничтожить сетевой объект может только владелец или мастер.

В офлайн-режиме `RequestOwnership()` сразу делает вас владельцем ([офлайн-режим](offline-mode.md)).

В примере Basic есть общий куб Takeover: нажмите `E`, чтобы забрать его, затем двигайте стрелками.
