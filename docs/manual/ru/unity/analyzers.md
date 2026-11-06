# Анализаторы

В пакет входят Roslyn-генератор исходников и анализаторы в одной DLL, `Runtime/Analyzers/NetPulse.Generators.dll`. Unity применяет её к `NetPulse.Runtime` и к каждой сборке, которая на него ссылается. Генератор пишет код сериализации и диспетчеризации для `[NetRpc]`, `SyncVar<T>`, `[NetSerializable]` и `INetMessage`. Анализаторы проверяют ваш код по правилам, которые это делают возможным, и сообщают о проблемах диагностиками компилятора с идентификаторами от `NP0001` до `NP0011`. Они видны в консоли Unity и в любой IDE, которая загружает Roslyn-анализаторы.

| Id | Серьёзность | Правило |
|---|---|---|
| NP0001 | Error | Класс с `[NetRpc]`, `SyncVar<T>` или `[NetSerializable]` не объявлен `partial` |
| NP0002 | Error | `[NetRpc]` вне `NetworkBehaviour` |
| NP0003 | Error | Неподдерживаемый тип параметра RPC |
| NP0004 | Error | RPC возвращает значение или имеет параметры `ref`, `out` или `in` |
| NP0005 | Error | Перегрузка RPC |
| NP0006 | Error | Неподдерживаемый член в типе `[NetSerializable]` или `INetMessage` |
| NP0007 | Error | `SyncVar<T>` с неподдерживаемым `T` |
| NP0008 | Warning | Поле `SyncVar<T>` не `readonly` или находится вне `NetworkBehaviour` |
| NP0009 | Error | Коллизия идентификаторов (RPC в одном типе, сообщения и сериализуемые типы в одной сборке) |
| NP0010 | Error | `[Synced]` на поле неподдерживаемого типа |
| NP0011 | Warning | `CallRpc("Name", ...)` при наличии метода `[NetRpc]` с таким именем |

Ниже для каждого правила показан минимальный код, который его вызывает, и затем исправление. Типы, с которыми работает генератор, объявляются `partial`, при этом `sealed partial` допустим.

## NP0001: тип должен быть partial

Генератор добавляет члены в ваш класс из второго файла, а это возможно, только если класс объявлен `partial`. То же относится к типу, который содержит такой класс.

```csharp title="NP0001"
using NetPulse;

public sealed class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }
}
```

Добавьте `partial`:

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }
}
```

## NP0002: [NetRpc] вне NetworkBehaviour

RPC принадлежат сетевым объектам, поэтому класс должен наследовать `NetworkBehaviour`.

```csharp title="NP0002"
using NetPulse;
using UnityEngine;

public sealed partial class Helper : MonoBehaviour
{
    [NetRpc]
    private void Ping()
    {
    }
}
```

```csharp
using NetPulse;

public sealed partial class Helper : NetworkBehaviour
{
    [NetRpc]
    private void Ping()
    {
    }
}
```

## NP0003: неподдерживаемый тип параметра RPC

Параметры используют типы из раздела [сериализуемые типы](messages.md#serializable-types). `GameObject` среди них нет.

```csharp title="NP0003"
using NetPulse;
using UnityEngine;

public sealed partial class Spawner : NetworkBehaviour
{
    [NetRpc]
    private void Spawn(GameObject prefab)
    {
    }
}
```

Отправьте `NetworkObject` либо передайте идентификатор и найдите объект на стороне получателя.

```csharp
using NetPulse;

public sealed partial class Spawner : NetworkBehaviour
{
    [NetRpc]
    private void Spawn(NetworkObject target)
    {
    }
}
```

`NetRpcInfo` допустим только последним параметром. Если поставить его в другое место, это тоже вызовет NP0003.

## NP0004: недопустимая сигнатура RPC

RPC представляет собой односторонний вызов. Он должен возвращать `void` и принимать параметры по значению.

```csharp title="NP0004"
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private int Hit(int damage) => damage;
}
```

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }
}
```

Если вызывающему нужен ответ, сделайте ответ ещё одним RPC, который идёт обратно отправителю (`Rpc.To(info.Sender)`).

## NP0005: перегрузка RPC

Идентификатор RPC вычисляется как хеш имени метода, поэтому два метода с одним именем невозможно различить.

```csharp title="NP0005"
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }

    [NetRpc]
    private void Hit(float damage)
    {
    }
}
```

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void Hit(int damage)
    {
    }

    [NetRpc]
    private void HitFloat(float damage)
    {
    }
}
```

## NP0006: неподдерживаемый член сериализуемого типа

Публичные поля и автосвойства типа `[NetSerializable]` или `INetMessage` должны быть поддерживаемых типов. Обобщённые типы не поддерживаются, а классу нужен конструктор без параметров.

```csharp title="NP0006"
using NetPulse;
using UnityEngine;

[NetSerializable]
public partial struct Loadout
{
    public GameObject Prefab;
    public int Slot;
}
```

```csharp
using NetPulse;

[NetSerializable]
public partial struct Loadout
{
    public NetworkObject Prefab;
    public int Slot;
}
```

## NP0007: неподдерживаемый тип SyncVar

`SyncVar<T>` принимает те же типы, что и сообщения. Чтобы использовать свой тип, пометьте его `[NetSerializable]`.

```csharp title="NP0007"
using NetPulse;
using UnityEngine;

public sealed partial class Turret : NetworkBehaviour
{
    private readonly SyncVar<GameObject> _target = new();
}
```

```csharp
using NetPulse;

public sealed partial class Turret : NetworkBehaviour
{
    private readonly SyncVar<NetworkObject> _target = new();
}
```

## NP0008: форма поля SyncVar

Поле `SyncVar<T>` должно быть `readonly`, потому что замена поля отвяжет его от сети. Поле вне `NetworkBehaviour` не синхронизируется никогда. Это предупреждение.

```csharp title="NP0008"
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    private SyncVar<int> _health = new(100);
}
```

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    private readonly SyncVar<int> _health = new(100);
}
```

## NP0009: коллизия идентификаторов

Идентификаторы представляют собой 16-битные хеши имён, поэтому два разных имени могут получить один идентификатор. Анализатор сообщает об этом, когда сталкиваются два RPC одного класса и когда сталкиваются два типа сообщений или `[NetSerializable]` в одной сборке. Переименуйте один из них.

```csharp title="NP0009"
using NetPulse;

public sealed partial class Lamp : NetworkBehaviour
{
    [NetRpc]
    private void SendTarget(int id)
    {
    }

    [NetRpc]
    private void ResetLight(int id)
    {
    }
}
```

```csharp
using NetPulse;

public sealed partial class Lamp : NetworkBehaviour
{
    [NetRpc]
    private void SendTarget(int id)
    {
    }

    [NetRpc]
    private void ResetLamp(int id)
    {
    }
}
```

Пример настоящий: оба имени дают один и тот же идентификатор. Коллизии редки, а когда она случается, диагностика называет оба члена и общий идентификатор.

## NP0010: неподдерживаемое поле Synced

Поля `[Synced]` поддерживают только примитивы, `string`, несколько структур Unity и перечисления (см. [синхронизация состояния](state-sync.md#synced-fields)). Для других типов используйте `SyncVar<T>`.

```csharp title="NP0010"
using System.Collections.Generic;
using NetPulse;

public sealed class Backpack : NetworkBehaviour
{
    [Synced] private List<int> _slots = new();
}
```

```csharp
using System;
using NetPulse;

public sealed partial class Backpack : NetworkBehaviour
{
    private readonly SyncVar<int[]> _slots = new(Array.Empty<int>());
}
```

## NP0011: используйте сгенерированный Rpc

`CallRpc("Name", ...)` с литералом имени по-прежнему работает, но использует рефлексию и не проверяется компилятором. Если существует подходящий метод `[NetRpc]` с таким именем, анализатор предлагает `Rpc.Name(...)`. Это предупреждение.

```csharp title="NP0011"
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void TakeDamage(int amount)
    {
    }

    public void Damage(int amount) => CallRpc("TakeDamage", RpcTarget.All, amount);
}
```

```csharp
using NetPulse;

public sealed partial class Hero : NetworkBehaviour
{
    [NetRpc]
    private void TakeDamage(int amount)
    {
    }

    public void Damage(int amount) => Rpc.TakeDamage(amount);
}
```

Старый вызов остаётся уместным, когда имя собирается во время работы, а анализатор этого не видит ([низкоуровневый API](low-level-api.md)).
