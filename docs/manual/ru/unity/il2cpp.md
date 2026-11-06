# IL2CPP

NetPulse работает с IL2CPP и с обрезкой управляемого кода (managed code stripping) на любом уровне. Атрибуты `[Preserve]` и собственный `link.xml` для методов `[NetRpc]`, полей `[Synced]`, подклассов `NetworkBehaviour`, типов `INetSerializable`, типов `[NetSerializable]` и типов `INetMessage` не нужны. Их сохраняют два механизма.

## Слой 1: атрибуты

`[NetRpc]`, `[Synced]` и `[NetSerializable]` наследуют `UnityEngine.Scripting.PreserveAttribute`. Документация Unity допускает наследование от этого атрибута и гарантирует, что линкер его учитывает, поэтому помеченный им член переживает обрезку сам по себе.

## Слой 2: сгенерированный link.xml

При сборке класс редактора `NetPulseLinkerProcessor` (реализация `IUnityLinkerProcessor`) находит ваш код через `TypeCache` Unity и записывает `Library/NetPulse/link.xml`. Линкер Unity читает этот файл вместе со своими. Процессор рассматривает сборки плеера без тестовых и для каждой вашей сборки сохраняет:

- каждый метод с атрибутом `[NetRpc]`, вместе с сигнатурой
- каждое поле `[Synced]`
- каждый неабстрактный подкласс `NetworkBehaviour`, вместе с его переопределением `OnSerialize` и сгенерированными методами `RegisterSyncVars` и `InvokeGeneratedRpc`
- каждый тип, реализующий `INetSerializable`
- каждый тип `[NetSerializable]` и каждый тип `INetMessage`, вместе с их сгенерированными методами `NetWrite` и `NetRead`

Три сборки пакета, `NetPulse.Runtime`, `NetPulse.Common` и `NetPulse.LiteNetLib`, сохраняются целиком. Для них нужна явная запись, потому что Unity не читает файлы `link.xml`, лежащие внутри пакетов.

Примерный вид результата:

```xml
<linker>
  <assembly fullname="NetPulse.Runtime" preserve="all" />
  <assembly fullname="NetPulse.Common" preserve="all" />
  <assembly fullname="NetPulse.LiteNetLib" preserve="all" />
  <assembly fullname="Assembly-CSharp">
    <type fullname="Hero">
      <method signature="System.Void Hit(System.Int32,UnityEngine.Vector3,NetPulse.NetRpcInfo)" />
      <method name="InvokeGeneratedRpc" />
    </type>
  </assembly>
</linker>
```

## Предпросмотр

Выберите `NetPulse > Tools > Preview IL2CPP link.xml`. Редактор запишет то же содержимое, что использовала бы сборка, в `Library/NetPulse/link.preview.xml`, выведет его в консоль и покажет файл. Так можно проверить, что нужный вам тип попал в список, например после переноса кода в новую assembly definition.

Тип попадает в список, только если его сборка входит в сборку плеера. Сборки только для редактора и тестовые сборки исключаются намеренно.

## Если чего-то всё равно не хватает

Если вызов через рефлексию не находит метод RPC, ошибка называет тип и метод и возвращает к этому механизму. На практике это значит, что собственный шаг сборки обошёл линкер-процессор NetPulse (например, конвейер сборки, который заменяет дополнительные файлы линкера). Проверьте предпросмотр и убедитесь, что `Library/NetPulse/link.xml` передаётся линкеру.

Для сгенерированных RPC отсутствие диспетчеризации обычно имеет другую причину: класс не `partial` или генератор исходников не активен для сборки. Тогда об этом говорит сама ошибка. См. [анализаторы](analyzers.md) и [решение проблем](../troubleshooting.md).
