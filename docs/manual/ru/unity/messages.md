# Сообщения

Проще всего сообщить всей комнате о том, что не относится к конкретному объекту (строка чата, уведомление «раунд начался», голосование), с помощью типизированного сообщения. Вы описываете структуру или класс, отправляете его через `Pulse.Send` и принимаете через `Pulse.On<T>`. Кодов событий и вызовов регистрации нет, потому что код сериализации генератор исходников пишет на этапе компиляции.

## Объявление, отправка, приём

Объявите сообщение как `partial`-тип, реализующий `INetMessage`:

```csharp
using System;
using NetPulse;
using UnityEngine;

public partial struct ChatMessage : INetMessage
{
    public string Text;
    public int Channel;
}

public sealed class Chat : MonoBehaviour
{
    private IDisposable _subscription;

    private void OnEnable() => _subscription = Pulse.On<ChatMessage>((message, sender) => Debug.Log($"{sender?.NickName}: {message.Text}"));

    private void OnDisable() => _subscription?.Dispose();

    public void Say(string text) => Pulse.Send(new ChatMessage { Text = text, Channel = 1 }, RpcTarget.All);
}
```

`Pulse.On<T>` возвращает `IDisposable`. Вызовите у него `Dispose`, чтобы отписаться. Обработчик получает сообщение и `Player`, который его отправил. Отправитель может быть `null`, если игрок уже вышел. Подписываться можно в любой момент, в том числе до входа в комнату.

## Отправка

У `Pulse.Send` три формы. Вызывать их нужно, находясь в комнате; иначе в лог попадёт предупреждение и ничего не отправится.

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public partial struct ChatMessage : INetMessage
{
    public string Text;
}

public sealed class SendExamples : MonoBehaviour
{
    private void Send(Player somePlayer)
    {
        Pulse.Send(new ChatMessage { Text = "hi" });
        Pulse.Send(new ChatMessage { Text = "all" }, RpcTarget.All);
        Pulse.Send(new ChatMessage { Text = "psst" }, somePlayer);
        Pulse.Send(new ChatMessage { Text = "welcome" }, RpcTarget.OthersBuffered);
        Pulse.Send(new ChatMessage { Text = "fast" }, RpcTarget.Others, new MessageOptions { Delivery = DeliveryMode.Unreliable });
    }
}
```

| Перегрузка | Значение |
|---|---|
| `Send(message)` | Всем остальным (`RpcTarget.Others`) |
| `Send(message, target)` | Группе получателей |
| `Send(message, target, options)` | То же, с `MessageOptions` |
| `Send(message, player, options)` | Одному игроку |

Получатели:

| `RpcTarget` | Кому доставляется |
|---|---|
| `Others` | Всем, кроме вас |
| `All` | Всем, включая вас. Ваши собственные обработчики срабатывают сразу, внутри вызова `Send`. |
| `MasterClient` | Мастеру. Если мастер вы, срабатывают только ваши обработчики, и ничего не отправляется. |
| `OthersBuffered` | Остальным сейчас и каждому, кто войдёт позже (сервер кэширует сообщение) |
| `AllBuffered` | Как `OthersBuffered`, плюс срабатывают ваши обработчики |
| `Owner` | Для сообщений недопустим (объекта нет). В лог попадёт предупреждение. |

Отправка `Player`, который является локальным игроком, запускает только ваши собственные обработчики.

## MessageOptions

У `MessageOptions` две настройки:

- `Delivery`: `ReliableOrdered` (по умолчанию), `ReliableUnordered`, `UnreliableSequenced` или `Unreliable`. Ненадёжный режим подходит для данных, где потеря пакета не важна, потому что следом придёт более свежий.
- `Caching`: `DoNotCache` (по умолчанию) или другое значение `CachingOption`. Буферизованные получатели выше включают кэш сами. `Caching = RemoveFromRoomCache` удаляет из комнаты все кэшированные сообщения, всех типов, а само это сообщение не доставляется.

Группы интереса на сообщения не действуют.

## Сериализуемые типы {#serializable-types}

Тип сообщения и любой вложенный в него тип описывается своими публичными полями экземпляра и публичными свойствами, у которых есть и геттер, и сеттер и которые записаны как автосвойства. Они пишутся в порядке объявления. Поля только для чтения, константы, статические члены и вычисляемые свойства пропускаются.

Поддерживаемые типы полей:

- `bool`, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `char`, `string`, `Guid` и перечисления
- `Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Color`, `Color32`, `Vector2Int`, `Vector3Int`, `Rect`
- `Player` и `NetworkObject` (передаются как номер актора или view id; если игрок или объект на стороне получателя неизвестен, придёт `null`)
- одномерные массивы и `List<T>` из любых перечисленных типов
- другие типы с атрибутом `[NetSerializable]` или реализующие `INetMessage`

```csharp
using System.Collections.Generic;
using NetPulse;
using UnityEngine;

[NetSerializable]
public partial struct Stats
{
    public int Hp;
    public bool Alive;
}

public partial class RoundSummary : INetMessage
{
    public Stats Winner;
    public Vector3[] Spawns;
    public List<string> Names;
    public Player Mvp { get; set; }
}
```

`[NetSerializable]` работает по тому же механизму для типов, которые сообщениями не являются. Применяйте его для значений, которые вы передаёте аргументами RPC, храните в свойствах или кладёте внутрь других сообщений. Правила такие:

- Тип должен быть `partial`, как и каждый тип, который его содержит.
- Он не может быть обобщённым.
- Классу нужен конструктор без параметров. Класс пишется с ведущим флагом null, поэтому `null`-экземпляр класса приходит как `null`. У структуры такого флага нет.
- `null` и пустые массивы или списки на проводе выглядят одинаково, а `null`-коллекция приходит как пустая.

Нарушение анализатор сообщает как ошибку компиляции ([анализаторы](analyzers.md)). Идентификатор сообщения вычисляется как хеш полного имени типа, поэтому переименование или перенос типа меняет его, и обе стороны должны работать на одной версии. Получатель, который встретил неизвестный идентификатор, пишет в лог одно предупреждение и игнорирует сообщение.

## Обработчики

- Для одного типа можно зарегистрировать несколько обработчиков. Они выполняются в порядке добавления.
- Исключение в обработчике попадает в лог, остальные обработчики всё равно выполняются.
- Сообщения, пришедшие во время загрузки сетевой сцены, удерживаются и проигрываются после неё ([сцены](scenes.md)).
