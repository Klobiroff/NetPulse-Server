# Синхронизация состояния

Синхронизация состояния держит значения сетевого объекта одинаковыми у всех клиентов. Для большинства случаев в NetPulse есть `SyncVar<T>`, а для передачи собственных данных потоком служат поля `[Synced]` и `OnSerialize`. Встроенные компоненты вроде `NetworkTransform` работают на том же механизме ([компоненты](components.md)).

## Что выбрать

| Задача | Что использовать |
|---|---|
| Значение с колбэком об изменении: здоровье, счёт, цвет, имя | `SyncVar<T>` |
| Значение, которое может менять только мастер | `SyncVar<T>` с `SyncPermission.Master` |
| Значение, заданное один раз и читаемое новичками | [Свойство комнаты или игрока](rooms-and-properties.md), если оно не привязано к объекту, иначе `SyncVar<T>` |
| Одноразовое событие на объекте | [RPC](rpc.md) |
| Постоянно меняющиеся данные, которые вы пишете вручную (направление прицела, сжатый путь) | `OnSerialize` с `NetStream` |
| Несколько примитивов без колбэков в старом коде | Поля `[Synced]` |

В новом коде начинайте с `SyncVar<T>`. Он типобезопасен, поддерживает гораздо больше типов, чем `[Synced]`, и сообщает, когда значение изменилось.

## SyncVar

```csharp
using NetPulse;
using UnityEngine;

public sealed partial class Hero : NetworkBehaviour
{
    private readonly SyncVar<int> _health = new(100);
    private readonly SyncVar<string> _title = new(string.Empty, SyncPermission.Master);

    public int Health => _health.Value;

    public override void OnNetworkSpawned() => _health.Changed += (previous, current) => Debug.Log($"{name}: {previous} -> {current}");

    public void Damage(int amount) => _health.Value -= amount;

    public void Rename(string title) => _title.Value = title;
}
```

Правила:

- Объявляйте поле как `readonly` внутри `partial`-класса, который наследует `NetworkBehaviour`. Генератор исходников его зарегистрирует. Анализатор предупреждает о поле без `readonly` и о поле вне `NetworkBehaviour` (NP0008), а недопустимый `T` отклоняет (NP0007).
- `T` может быть любым типом из списка [сериализуемых типов](messages.md#serializable-types): примитивы, строки, перечисления, векторы и цвета Unity, `Player`, `NetworkObject`, массивы, списки и типы `[NetSerializable]`.
- Читайте через `.Value` или полагайтесь на неявное преобразование в `T`.
- `Changed(previous, current)` срабатывает у каждого клиента при изменении значения, как для ваших записей, так и для значений, пришедших по сети. Исключение в обработчике попадает в лог и не останавливает остальные обработчики.
- Присваивание значения, равного текущему, ничего не делает. Сравнение идёт через `EqualityComparer<T>.Default`, поэтому массивы и списки сравниваются по ссылке. Изменение содержимого списка на месте не замечается; чтобы опубликовать изменение, присвойте новый список.

### Права записи {#permissions}

| Право | Кто может писать | Как передаётся |
|---|---|---|
| `Owner` (по умолчанию) | Владелец объекта | Дельтой внутри обычного обновления состояния объекта, на границе тика |
| `Master` | Мастер-клиент | Отдельным сообщением, которое уходит в следующем кадре после записи |

Запись от того, у кого нет права, игнорируется, а в лог пишется одно предупреждение на поле. Пока объект ещё не создан в сети или вы не в комнате, запись принимается, поэтому начальные значения можно задавать свободно. Сервер тоже отклоняет обновления состояния от не-владельцев и master-переменные от не-мастеров.

У поведения может быть до 32 переменных `Owner`. Лишние не синхронизируются, а в лог пишется ошибка. Общий предел по обоим видам составляет 256 на поведение.

### Опоздавшие игроки

Игрок, вошедший позже, получает полное значение каждой переменной: владельцы повторно отправляют полное состояние, когда кто-то входит, а мастер отправляет новичку свои переменные. Для чужого объекта, у поведений которого есть состояние, `OnNetworkSpawned` ждёт применения этого первого состояния, но не дольше секунды, так что колбэк сразу видит настоящие данные ([жизненный цикл](lifecycle.md)).

## Потоковая передача через OnSerialize

Переопределите `OnSerialize(NetStream stream)`, чтобы писать и читать собственные байты. У владельца в вызове `stream.IsWriting` истинно, а у всех остальных клиентов истинно `stream.IsReading`. Форма `Serialize(ref value)` симметрична, поэтому одно и то же тело обслуживает оба направления.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class AimSync : NetworkBehaviour
{
    private Vector3 _aim;
    private int _ammo;
    private float _heat;

    public Vector3 Aim => _aim;

    public int Ammo => _ammo;

    public float Heat => _heat;

    protected override DeliveryMode SerializeDelivery => DeliveryMode.UnreliableSequenced;

    public override void OnSerialize(NetStream stream)
    {
        stream.Serialize(ref _aim);
        stream.Serialize(ref _ammo);
        stream.Serialize(ref _heat);
    }
}
```

Что есть в `NetStream`:

- `Serialize(ref x)` для `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `string`, `byte[]`, `Vector2`, `Vector3`, `Quaternion` и `Color`, а также `Serialize(INetSerializable)` для ваших типов, реализующих `Serialize(NetStream)`.
- `SendNext(object)` и `ReceiveNext<T>()` в стиле PUN. Читаемый тип должен точно совпадать с записанным, иначе вы получите `default`.
- `IsWriting`, `IsReading`, `Remaining` (сколько байт осталось прочитать) и `Timestamp`, серверное время, когда владелец сформировал состояние.

```csharp
using NetPulse;
using UnityEngine;

public sealed class PunStyleSync : NetworkBehaviour
{
    private int _ammo;
    private Vector3 _lookAt;

    public override void OnSerialize(NetStream stream)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(_ammo);
            stream.SendNext(_lookAt);
            return;
        }

        _ammo = stream.ReceiveNext<int>();
        _lookAt = stream.ReceiveNext<Vector3>();
    }
}
```

Как это отправляется: на каждом тике отправки владелец вызывает `OnSerialize`, сравнивает байты с предыдущими и отправляет только при отличии. Поскольку метод вызывается на каждом тике отправки для каждого своего объекта, делайте его лёгким и избегайте выделения памяти.

Переопределите `SerializeDelivery`, чтобы выбрать режим доставки состояния поведения. По умолчанию это `ReliableOrdered`. Для `UnreliableSequenced` и `Unreliable` состояние дополнительно отправляется ещё раз после секунды тишины, чтобы потерянный пакет не оставил клиента с устаревшим значением надолго. Поведения с разными режимами доставки на одном объекте уходят отдельными пакетами.

## Поля Synced {#synced-fields}

`[Synced]` помечает поле `NetworkBehaviour` для автоматической синхронизации, без колбэка:

```csharp
using NetPulse;
using UnityEngine;

public sealed class Scoreboard : NetworkBehaviour
{
    [Synced] private int _score;
    [Synced] private Color _tint = Color.white;

    public int Score => _score;

    public Color Tint => _tint;

    public void Add(int points) => _score += points;
}
```

Поддерживаются `bool`, `byte`, `short`, `int`, `long`, `float`, `double`, `string`, `Vector2`, `Vector3`, `Quaternion`, `Color`, `Vector2Int`, `Vector3Int` и перечисления; остальные анализатор отклоняет (NP0010). Поля сортируются по имени, их не больше 32 на поведение, а владелец на каждом тике отправки опрашивает их через рефлексию и отправляет изменившиеся. Принимающий клиент записывает поле напрямую, поэтому события вы не получите и должны опрашивать поле сами, например в `Update`. Если нужно реагировать на изменение, предпочтите `SyncVar<T>`.

## Частота отправки

Состояние уходит на границах тиков, только от владельца и только для созданных в сети объектов. Частота равна меньшему из значений `Send Rate` и `Serialization Rate` в настройках, где `Serialization Rate = 0` означает частоту тиков сервера. Арифметика показана на странице [тики и время](ticks-and-time.md). Изменение, сделанное в `OnNetworkTick`, попадает в обновление, которое отправляется в конце того же тика.
