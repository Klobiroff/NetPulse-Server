# Жизненный цикл

`NetworkBehaviour` добавляет к обычным колбэкам Unity набор виртуальных хуков. На этой странице перечислены все хуки, момент их вызова и то, что гарантируется про порядок. Всё выполняется в главном потоке.

## Хуки

| Хук | Когда вызывается |
|---|---|
| `OnNetworkSpawned()` | Один раз, когда объект стал живым в комнате |
| `OnNetworkTick(long tick)` | На каждом сетевом тике, пока объект создан в сети ([тики и время](ticks-and-time.md)) |
| `OnOwnerChanged(Player previous, Player current)` | При смене владельца |
| `OnOwnershipGained()`, `OnOwnershipLost()` | Когда вы начинаете или перестаёте владеть объектом |
| `OnOwnershipRequest(Player requester)` | На клиенте владельца, когда кто-то просит объект режима `Request` ([владение](ownership.md)) |
| `OnMasterChanged(Player previous, Player current)` | При смене мастер-клиента |
| `OnSerialize(NetStream stream)` | При каждой отправке состояния (у владельца) и при каждом приёме (у остальных) ([синхронизация состояния](state-sync.md)) |
| `OnNetworkDespawned()` | При уничтожении объекта, если `OnNetworkSpawned` уже вызывался |

```csharp
using NetPulse;
using UnityEngine;

public sealed class HookProbe : NetworkBehaviour
{
    public override void OnNetworkSpawned() => Debug.Log($"{name}: spawned, mine={IsMine}, view {ViewId}");

    public override void OnNetworkDespawned() => Debug.Log($"{name}: despawned");

    public override void OnOwnerChanged(Player previous, Player current) => Debug.Log($"{name}: owner {previous?.NickName} -> {current?.NickName}");

    public override void OnOwnershipGained() => Debug.Log($"{name}: gained");

    public override void OnOwnershipLost() => Debug.Log($"{name}: lost");

    public override void OnMasterChanged(Player previous, Player current) => Debug.Log($"{name}: master {previous?.NickName} -> {current?.NickName}");
}
```

## Гарантии порядка

### Создание и появление в сети

1. Unity вызывает `Awake` и `OnEnable`. В `NetworkObject.Awake` объект собирает свои `NetworkBehaviour` (нумеруя их в порядке иерархии) и регистрирует их `SyncVar`. Порядок вызовов `Awake` у разных компонентов Unity не определяет, поэтому не читайте `ViewId` и `IsMine` в собственном `Awake`. Используйте `OnNetworkSpawned`.
2. `OnNetworkSpawned` вызывается ровно один раз на объект, для каждого поведения в порядке иерархии. Для объекта, который вы создаёте через `Pulse.Instantiate`, он уже отработал к моменту возврата из `Instantiate`. Для объекта, которым владеет кто-то другой, он обычно вызывается сразу после прихода сообщения о создании, в том же кадре, что `Awake` и `OnEnable`, и до `Start`.
3. Если объект принадлежит другому игроку и у него есть состояние для приёма (`SyncVar`, поля `[Synced]` или `OnSerialize`), `OnNetworkSpawned` ждёт, пока будет применено первое обновление состояния, но не дольше одной секунды. Тогда первое, что видит ваш код, настоящие данные, а не значения полей по умолчанию. В этом случае `Start` может выполниться раньше.
4. `OnNetworkSpawned` всегда вызывается раньше первого `OnNetworkTick` этого объекта. Объект, о появлении которого ещё не объявлено, тиков не получает.

Сценовые объекты регистрируются в `OnEnable`, находитесь вы в комнате или нет. Если в этот момент клиент вне комнаты, `IsMine` истинно и `OnNetworkSpawned` вызывается сразу. После входа не мастером `IsMine` становится ложным без какого-либо колбэка владения. Настройку, которая зависит от того, кто владеет сценовым объектом, делайте после `OnJoinedRoom`.

### Смена владельца

Сервер объявляет нового владельца. Затем для каждого поведения объекта, в порядке иерархии:

1. `OnOwnerChanged(previous, current)`
2. `OnOwnershipGained()`, если вы стали владельцем, или `OnOwnershipLost()`, если перестали им быть

Когда все поведения обработаны, объект поднимает `NetworkObject.OwnershipChanged`, а затем срабатывают `Pulse.Events.OnOwnershipChanged` (и `IOwnershipCallbacks.OnOwnershipChanged`). Событие поднимается один раз на объект, а не по разу на поведение. Внутри любого хука `IsMine` и `Owner` уже показывают нового владельца.

Объект, который опоздавший игрок только что создал и который ещё ждёт первого состояния, может получить `OnOwnerChanged` раньше `OnNetworkSpawned`. Не считайте в `OnOwnerChanged`, что хук создания уже отработал.

### Смена мастера

Когда сервер объявляет нового мастера:

1. `OnMasterChanged(previous, current)` вызывается для каждого поведения каждого объекта, у которого уже был вызван `OnNetworkSpawned`. Для сценового объекта без явного владельца (им владеет мастер) на том же поведении следом идут `OnOwnershipGained()` или `OnOwnershipLost()`, если вы стали мастером или перестали им быть.
2. После всех объектов поднимается `Pulse.Events.OnMasterClientSwitched(newMaster)`.

### Уничтожение

`OnNetworkDespawned` вызывается из `OnDestroy` компонента `NetworkObject` и только если `OnNetworkSpawned` уже вызывался. Порядок `OnDestroy` между компонентами Unity не фиксирует, поэтому собственный `OnDestroy` поведения может выполниться раньше или позже. Сетевые ресурсы освобождайте в `OnNetworkDespawned`, а обычные в `OnDestroy`.

## Где NetPulse выполняется в кадре

Скрытый объект-раннер (`[NetPulse]`, создаётся при первом обращении и сохраняется между сценами) обновляет клиента с порядком выполнения -1000, то есть раньше `Update` ваших скриптов:

1. Он опрашивает транспорт. Всё, что пришло, обрабатывается сейчас: создание объектов, RPC, обновления состояния, изменения свойств, события владения и мастера, а также ваши колбэки.
2. Он запускает цикл сетевых тиков: `OnNetworkTick` для каждого созданного объекта, затем отправка состояния этого тика ([тики и время](ticks-and-time.md)).
3. После этого выполняются `Update`, `LateUpdate` и корутины ваших скриптов, и они видят данные, пришедшие на шаге 1.

Пока загружается сетевая сцена, события комнаты ставятся в очередь и затем выдаются в исходном порядке ([сцены](scenes.md)).
