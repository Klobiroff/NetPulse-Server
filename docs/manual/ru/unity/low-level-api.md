# Низкоуровневый API

В повседневном коде эта страница не нужна. Прежний API со строками и байтовыми кодами по-прежнему поддерживается без изменений, и с ним вы сталкиваетесь при сопровождении кода версии 0.2, при написании инструментов или когда нужно то, чего нет в типизированном API. Здесь собраны RPC через рефлексию, сырые события с байтовыми кодами, сериализаторы, зарегистрированные вручную, группы интереса и транспорт.

## RPC через рефлексию {#reflection-rpcs}

`CallRpc(methodName, target, arguments)` вызывает метод `[NetRpc]` по имени. Используйте его, когда имя известно только во время работы, как в `health.Send("Heal", 5)` ниже:

```csharp
using NetPulse;
using UnityEngine;

public sealed partial class Health : NetworkBehaviour
{
    [Synced] private int _hp = 100;

    public int Hp => _hp;

    public void Send(string rpcName, int amount) => CallRpc(rpcName, RpcTarget.All, amount);

    [NetRpc]
    private void TakeDamage(int amount, NetRpcInfo info)
    {
        _hp -= amount;
        Debug.Log($"{info.Sender?.NickName} dealt {amount}");
    }

    [NetRpc]
    private void Heal(int amount) => _hp += amount;
}
```

Чем это отличается от сгенерированного `Rpc.X()`:

- Метод находится через рефлексию, а аргументы передаются как значения с динамическим типом. Это могут быть любые значения, допустимые в `PropertyTable` ([комнаты и свойства](rooms-and-properties.md#the-raw-propertytable)), плюс зарегистрированные типы. Получатель приводит их к типам параметров.
- Получателей задаёт переданный вами аргумент. Получатели из атрибута не используются. `Delivery` и `OwnerOnly` из атрибута учитываются.
- Неверное число аргументов пишет ошибку в лог и ничего не отправляет. Неизвестное имя пишет ошибку с указанием вероятной причины (опечатка, отсутствие атрибута или обрезка IL2CPP, см. [IL2CPP](il2cpp.md)).
- Если имя задано литералом или выражением `nameof` и существует подходящий метод `[NetRpc]` с таким именем, анализатор сообщает NP0011 и предлагает сгенерированный вызов, например `Rpc.TakeDamage(amount)`.

И RPC через рефлексию, и сгенерированные RPC передаются как событие 242, с разным байтом вида и разной раскладкой аргументов ([сетевой протокол](../reference/protocol.md)).

## Сырые события

`Pulse.RaiseEvent(code, data, options)` отправляет событие с байтовым кодом от 0 до 199. Больший код бросает `ArgumentOutOfRangeException`. Коды от 230 до 250 принадлежат системе: коды 230 до 237 заняты серверными уведомлениями, коды 240 до 245 несут создание и уничтожение объектов, RPC, состояние, типизированные сообщения и master-переменные, а код 250 сообщает об ошибках.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class Flares : NetPulseCallbacks
{
    private const byte FlareEvent = 10;

    public void Launch(Vector3 position)
    {
        var options = new RaiseEventOptions
        {
            Receivers = ReceiverGroup.All,
            Delivery = DeliveryMode.ReliableUnordered
        };
        Pulse.RaiseEvent(FlareEvent, position, options);
    }

    public override void OnEvent(byte eventCode, int senderActorId, object data)
    {
        if (eventCode == FlareEvent && data is Vector3 position)
        {
            Debug.Log($"Flare from actor {senderActorId} at {position}");
        }
    }
}
```

`RaiseEventOptions`:

| Свойство | По умолчанию | Значение |
|---|---|---|
| `Receivers` | `Others` | `Others`, `All` (включая отправителя), `MasterClient` или `Targets` (акторы из `TargetActors`) |
| `TargetActors` | нет | Номера акторов для `Targets` |
| `InterestGroup` | `0` | Ограничение группой, см. ниже |
| `Caching` | `DoNotCache` | Режим кэширования, см. ниже |
| `Delivery` | `ReliableOrdered` | Режим транспорта |

Событие приходит как `Pulse.Events.OnEvent(code, senderActorId, data)` или через `IOnEventCallback`. Туда попадают только ваши собственные коды от 0 до 199. Вне комнаты `RaiseEvent` ничего не делает.

Варианты кэширования:

| `CachingOption` | Эффект |
|---|---|
| `DoNotCache` | Только доставка |
| `AddToRoomCache` | Доставка и сохранение. Сервер проигрывает сохранённые события игрокам, которые войдут позже. Они удаляются, когда их отправитель выходит. |
| `AddToRoomCacheGlobal` | Как `AddToRoomCache`, но событие остаётся после выхода отправителя |
| `RemoveFromRoomCache` | Удалить все сохранённые события с этим кодом. Ничего не доставляется. |
| `RemoveFromCacheForActorsLeft` | Удалить сохранённые события, отправителя которых уже нет в комнате. Ничего не доставляется. |

## Свои типы и NetTypeRegistry

`data` и аргументами RPC могут быть `null`, `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `string`, `Guid`, `byte[]`, `int[]`, `float[]`, `string[]`, `object[]`, `PropertyTable` или `IDictionary<string, object>` со строковыми ключами. Типы Unity `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int` и `Vector3Int` регистрируются при старте под кодами типов от 100 до 105. Другие типы нужно зарегистрировать, иначе отправка бросит исключение сериализации.

В новом коде пометьте тип атрибутом `[NetSerializable]`, и генератор зарегистрирует его сам ([сообщения](messages.md#serializable-types)). Чтобы зарегистрировать тип вручную, возьмите свободный код от 100 до 199 и используйте один и тот же код на всех клиентах:

```csharp
using NetPulse.Common;
using UnityEngine;

public struct Checkpoint
{
    public int Index;
    public float Time;
}

public static class CheckpointSerializer
{
    public const byte Code = 120;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register() => NetTypeRegistry.Register<Checkpoint>(
        Code,
        static (writer, value) =>
        {
            writer.WriteInt(value.Index);
            writer.WriteFloat(value.Time);
        },
        static reader => new Checkpoint { Index = reader.ReadInt(), Time = reader.ReadFloat() });
}
```

У `NetTypeRegistry` есть также `RegisterExtended<T>(ushort id, ...)` для 16-битных идентификаторов, которые использует сгенерированный код, а ещё `IsRegistered(code)` и `Unregister(code)`. Повторная регистрация кода для другого типа бросает исключение. У `NetWriter` и `NetReader` есть `WriteInt`, `WriteFloat`, `WriteString` и парные методы `Read` для всех примитивов, а методы-расширения NetPulse добавляют типы Unity.

## Состояние вручную

Поля `[Synced]`, `OnSerialize(NetStream)` и `SerializeDelivery` относятся к этому же старому слою и полностью описаны на странице [синхронизация состояния](state-sync.md). Строковые ключи свойств (`room.SetProperty("map", "forest")`, `SetProperties(delta, expected)`) рассмотрены в разделе [комнаты и свойства](rooms-and-properties.md#the-raw-propertytable).

## Группы интереса {#interest-groups}

Группа интереса ограничивает круг получателей события. Группа `0` означает всех. Событие с другим номером группы получают только игроки, подписавшиеся на неё:

```csharp
using NetPulse;
using UnityEngine;

public sealed class Zone : MonoBehaviour
{
    [SerializeField] private byte _group = 5;

    public void Enter()
    {
        var add = new byte[] { _group };
        Pulse.ChangeInterestGroups(add, null);
    }

    public void Exit()
    {
        var remove = new byte[] { _group };
        Pulse.ChangeInterestGroups(null, remove);
    }
}
```

- Группу задают событию через `RaiseEventOptions.InterestGroup`, сетевому объекту через его поле `Interest Group`, а созданию объекта через аргумент `interestGroup` метода `Pulse.Instantiate`.
- Фильтрация по группе действует для `Others` и `All`. События для мастер-клиента или для конкретных акторов её игнорируют.
- Для подписки нужна комната, а в офлайн-режиме подписка ничего не делает. Подписки сбрасываются, когда игрок становится неактивным.
- Кэшированные события проигрываются один раз, при входе игрока, и только те, чья группа у игрока в этот момент есть. Подписка позже не возвращает то, что было закэшировано раньше.

## Транспорт и протокол {#transport-and-protocol}

`Pulse.UseTransport(INetTransport)` заменяет стандартный `LiteNetLibTransport`. В интерфейсе есть `Connect`, `Send`, `Poll` и `Disconnect`, события `Connected`, `Disconnected` и `DataReceived`, а также свойства `IsConnected` и `RoundTripTimeMs`. Заменять транспорт нужно в отключённом состоянии. Формат данных на проводе описан в разделе [сетевой протокол](../reference/protocol.md).
