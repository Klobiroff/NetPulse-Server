# Настройки и редактор

На этой странице перечислены все поля `NetPulseSettings`, а затем окна редактора и пункты меню, которые добавляет NetPulse.

## NetPulseSettings

Настройки хранятся в ассете `ScriptableObject`. Во время работы клиент загружает его через `Resources.Load("NetPulseSettings")`, поэтому ассет должен лежать в папке `Resources`. Стандартное расположение: `Assets/Resources/NetPulseSettings.asset`. Если ассета нет, клиент использует временный экземпляр со значениями по умолчанию из таблиц ниже. Чтобы использовать другой ассет из кода, передайте его в `Pulse.Connect(settings)` ([подключение](connecting.md#connection-settings)).

Создать ассет можно через `NetPulse > Create Settings Asset`, кнопкой в `Project Settings > NetPulse` или через `Assets > Create > NetPulse > Settings`.

### Server

| Поле | По умолчанию | Значение |
|---|---|---|
| Host | `127.0.0.1` | Адрес сервера (IP или имя хоста) |
| Port | `5055` | UDP-порт сервера |
| App Id | пусто | Идентифицирует вашу игру. Если на сервере настроен `AppId`, значения должны совпадать. |
| App Version | `1.0` | Строка версии вашей сборки. Отправляется на сервер, который её не проверяет. |
| Connection Key | `NetPulse` | Общий секрет UDP-рукопожатия. Должен совпадать с `ConnectionKey` сервера. |

### Security

| Поле | По умолчанию | Значение |
|---|---|---|
| Encryption Mode | `Off` | `Off` отправляет открытые пакеты. `Required` шифрует всё и отказывается от открытых пакетов, см. [Подключение](connecting.md#encryption). |
| Server Public Keys | пусто | Один или несколько публичных ключей в Base64, которые печатает `NetPulse.Server keygen`. Инспектор и Hub проверяют формат. Несколько ключей нужны только для ротации ключа сервера. |
| Pre Shared Key | пусто | Необязательный ключ в Base64 (32 байта), который есть и у сервера. Он поставляется внутри сборки и может быть извлечён. |
| Rekey After Packets | `1000000` | Менять ключ отправки после стольких пакетов (`0` — не менять по числу) |
| Rekey After Ms | `600000` | Менять ключ отправки через столько миллисекунд (`0` — не менять по времени) |
| Replay Window | `4096` | Сколько пакетов помнить для защиты от повтора. Кратно 64, от 64 до 65536. |
| Padding Block | `0` | `0` — выключено, иначе от 16 до 512. Дополняет каждый пакет до кратного размера. Задавайте то же значение, что и на сервере. |
| Handshake Timeout | `5` (не меньше 0,5) | Сколько секунд ждать защищённого рукопожатия, прежде чем соединение завершится с `SecureHandshakeTimeout` |

### Player

| Поле | По умолчанию | Значение |
|---|---|---|
| Default Nick Name | пусто | Используется, если не задан `Pulse.NickName`. Если и он пуст, клиент выбирает `Player` со случайным числом. |
| Persist User Id | вкл. | Хранить созданный `UserId` в `PlayerPrefs`. Нужен для [возвращения в комнату](matchmaking.md#leaving-and-rejoining). |

### Rates

| Поле | По умолчанию | Значение |
|---|---|---|
| Send Rate | `20` (от 1 до 60) | Верхний предел отправок состояния в секунду |
| Serialization Rate | `0` (от 0 до 60) | Второй предел отправок состояния. `0` означает частоту тиков сервера. Эффективная частота равна меньшему из двух. |
| Request Timeout | `10` (не меньше 1) | Через сколько секунд запрос завершается ошибкой `Timeout` |

Как две частоты превращаются в интервал отправки, показано на странице [тики и время](ticks-and-time.md#how-ticks-drive-sending).

### Behaviour

| Поле | По умолчанию | Значение |
|---|---|---|
| Run In Background | вкл. | `Pulse.Connect` включает `Application.runInBackground`, чтобы соединение жило без фокуса |
| Auto Sync Scene | вкл. | Позволяет `Pulse.LoadScene` загружать сцену у каждого клиента ([сцены](scenes.md)) |
| Collect Stats | `Auto` | `Auto` собирает статистику трафика только в редакторе и в development-сборках. `Always` и `Never` переопределяют это. |
| Log Verbose | выкл. | Зарезервировано. Текущий клиент это поле не читает. |
| Prefab Registry | нет | Ассет `NetworkPrefabRegistry`, используемый для создания объектов. Создаётся и заполняется автоматически. |

В коде те же значения доступны как свойства `NetPulseSettings`, например `settings.SendRate = 30`. `CollectStats` там свойство типа `bool`: чтение применяет правило `Auto`, а запись выбирает `Always` или `Never`.

## Project Settings

`Edit > Project Settings > NetPulse` показывает все поля ассета. В нём две кнопки: `Rebuild Prefab Registry` и `Open Hub`. Если ассета нет, показывается уведомление и кнопка `Create Settings Asset`.

## NetPulse Hub

`NetPulse > Open Hub` открывает окно с двумя вкладками. Пока игра запущена, оно обновляется четыре раза в секунду.

Вкладка `Overview` показывает:

- Connection: состояние, онлайн или офлайн режим, пинг, серверное время, идентификатор пользователя и ник. В Play Mode есть кнопки `Connect` и `Disconnect`.
- Security: режим шифрования, число закреплённых ключей сервера, проблемы с форматом ключа и, в Play Mode, зашифрована ли сессия.
- Room: имя, число игроков относительно лимита, флаги открытости и видимости, число сетевых объектов и список игроков с пометками для вас, мастера и неактивных игроков.
- Traffic: сообщения и килобайты, отправленные и полученные, с текущей скоростью в килобайтах в секунду.
- Prefab registry: сколько префабов зарегистрировано и кнопка `Rebuild Prefab Registry`.

### Вкладка Traffic

Вкладка `Traffic` служит профайлером трафика работающего клиента. Ей нужен Play Mode и включённый `Collect Stats` (переключатель `Collect` вверху включает сбор на лету). `Reset` обнуляет счётчики. В верхней строке показаны текущие байты в секунду, во второй итоги за сессию.

В таблице одна строка на запись, колонки `Category`, `Name`, `In B/s`, `Out B/s`, `In total`, `Out total` и `Msgs in/out`. Нажмите на заголовок колонки, чтобы отсортировать по ней, и ещё раз, чтобы развернуть порядок. Выпадающий список и текстовое поле над таблицей фильтруют по категории и по имени.

Категории:

| Категория | Одна строка на |
|---|---|
| `NetworkObject` | Сетевой объект (`viewId name`): его создание, уничтожение, состояние и RPC-трафик |
| `Rpc` | Метод RPC (`Type.Method`) |
| `Message` | Тип сообщения |
| `StateSync` | Обновления состояния объектов (одна общая строка) |
| `SyncVar` | Тип поведения, которое отправляет переменные `Master` |
| `System` | Протокольный трафик: `Request op N`, `Response op N`, `Event N`, `Ping`, `Pong` |

Категории пересекаются намеренно. RPC учитывается и в `Rpc`, и в `NetworkObject`, которому он принадлежит, поэтому строки нельзя складывать. Колонки скорости считаются по окну в одну секунду.

Те же данные можно прочитать из кода:

```csharp
using NetPulse;
using UnityEngine;

public sealed class TrafficLog : MonoBehaviour
{
    private void Dump()
    {
        var stats = Pulse.Stats;
        Debug.Log($"in {stats.BytesInPerSecond:F0} B/s, out {stats.BytesOutPerSecond:F0} B/s, session {stats.TotalBytesIn + stats.TotalBytesOut} B");

        foreach (var entry in stats.Entries)
        {
            if (entry.Category == StatCategory.Rpc)
            {
                Debug.Log($"{entry.Name}: {entry.MessagesOut} sent, {entry.BytesOut} B");
            }
        }
    }
}
```

`NetStats` предоставляет `Enabled`, `Entries` (каждый `NetStatEntry` содержит `Category`, `Name`, `BytesIn`, `BytesOut`, `MessagesIn`, `MessagesOut`, `BytesInPerSecond` и `BytesOutPerSecond`), счётчики `Total...`, `BytesInPerSecond`, `BytesOutPerSecond` и `Reset()`. Когда сбор выключен, счётчики ничего не стоят.

## Инспектор NetworkObject

Инспектор `NetworkObject` показывает `Ownership Mode`, `Interest Group`, `Persist Across Scenes` и переключатель `Scene Object` с полем `Scene Id` (вне диапазона от 1 до 999 появляется ошибка). Переключатель сценового объекта отключён для ассетов префабов и при выделении нескольких объектов. Для ассета префаба выводится уведомление, что он автоматически попадает в реестр. В Play Mode блок `Runtime` только для чтения показывает `View Id`, `Owner Actor`, `Is Mine` и `Spawned`.

## Пункты меню

| Меню | Что делает |
|---|---|
| `NetPulse > Create Settings Asset` | Создаёт или выделяет ассет настроек |
| `NetPulse > Open Settings` | Открывает `Project Settings > NetPulse` |
| `NetPulse > Open Hub` | Открывает окно Hub |
| `NetPulse > Rebuild Prefab Registry` | Сканирует `Assets` на префабы с `NetworkObject` и обновляет реестр |
| `NetPulse > Tools > Preview IL2CPP link.xml` | Записывает и показывает сгенерированный `link.xml` ([IL2CPP](il2cpp.md)) |
| `NetPulse > Samples > Build Basic Sample Scene` | Собирает сцены примера Basic (сначала импортируйте пример, [установка](installation.md#the-basic-sample)) |
| `Assets > Create > NetPulse > Settings` и `Network Prefab Registry` | Создают эти два ассета вручную |
| `Add Component > NetPulse` | `Network Transform`, `Network Rigidbody`, `Network Animator` |

Реестр также пересобирается сам при импорте, перемещении или удалении префабов в `Assets` и перед каждой сборкой.
