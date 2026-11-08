# Установка

Клиент поставляется как Unity-пакет `com.netpulse.unity` (версия 0.4). Он поддерживает Unity от 2021.3 LTS до 6000.x, написан на обычном C# 9, работает в главном потоке и совместим и с Mono, и с IL2CPP. Кроме пакета нужен запущенный сервер NetPulse, см. [быстрый старт](../getting-started.md).

## Установка через Package Manager

1. Откройте `Window > Package Manager`.
2. Нажмите `+` и выберите `Install package from git URL...`.
3. Вставьте этот адрес и подтвердите:

```text
https://github.com/Klobiroff/NetPulse-Unity.git?path=Packages/com.netpulse.unity
```

Пока репозиторий приватный, Unity нужен доступ по git. Установите git, убедитесь, что `git` есть в `PATH`, и войдите в GitHub под аккаунтом, у которого есть доступ на чтение (на Windows и macOS это делает Git Credential Manager). Если Package Manager сообщает об ошибке аутентификации, выполните в терминале `git ls-remote https://github.com/Klobiroff/NetPulse-Unity.git` и проверьте, что учётные данные работают.

Ту же строку можно добавить в `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.netpulse.unity": "https://github.com/Klobiroff/NetPulse-Unity.git?path=Packages/com.netpulse.unity"
  }
}
```

Чтобы зафиксировать версию, добавьте в конец адреса `#` и тег, ветку или хеш коммита.

## Копирование папки

Если с доступом по git проблемы, скопируйте папку `com.netpulse.unity` в папку `Packages` вашего проекта. Unity воспринимает её как встроенный пакет. Копируйте папку целиком вместе с файлами `.meta`: от настроек импорта зависит работа DLL генератора в `Runtime/Analyzers`. Можно также выбрать в Package Manager пункт `Add package from disk...` и указать `package.json` из этой папки.

## Сборки

Пакет определяет три сборки: `NetPulse.Runtime`, `NetPulse.Common` (код протокола и сериализации, он синхронизируется из репозитория сервера, поэтому не правьте его) и `NetPulse.LiteNetLib` (встроенный транспорт, LiteNetLib 1.3.5). Скрипты в стандартной сборке `Assembly-CSharp` подключают их автоматически.

Если ваш код лежит в собственной assembly definition, добавьте ссылки на `NetPulse.Runtime` и `NetPulse.Common`. Генератор исходников работает для каждой сборки, которая ссылается на `NetPulse.Runtime`, а сгенерированный код использует типы из `NetPulse.Common`.

## Создание ассета настроек

Откройте `Project Settings > NetPulse` или выберите `NetPulse > Create Settings Asset`. Unity создаст `Assets/Resources/NetPulseSettings.asset`. Укажите хост, порт (по умолчанию UDP 5055), `AppId` и `AppVersion`. Если на сервере настроен `AppId`, значение клиента должно совпадать с ним. `AppVersion` задаёт вашу строку с версией сборки, сервер её не проверяет. Все поля описаны на странице [настройки и редактор](settings-and-editor.md).

Без ассета `Pulse.Connect()` всё равно работает со встроенными значениями по умолчанию (хост `127.0.0.1`, порт 5055), этого хватает для локальной проверки.

## Input System {#input-system}

Сам пакет ввод не читает. Читает его пример Basic, и его скрипты компилируются под тот вариант ввода, который выбран в проекте: при определённом `ENABLE_INPUT_SYSTEM` они используют пакет Input System, иначе старый Input Manager. Если в `Project Settings > Player > Active Input Handling` выбрано `Input System Package (New)` или `Both`, установите пакет `com.unity.inputsystem`, иначе скрипты примера не скомпилируются.

## Пример Basic {#the-basic-sample}

Пример построен на IMGUI. Он подключается, показывает список комнат, создаёт комнаты и входит в них, а также создаёт игрока с сгенерированными RPC, здоровьем на `SyncVar`, типизированным сообщением чата, типизированным счётом комнаты, движением по тикам, загрузкой сцены только мастером и общим кубом Takeover.

1. Откройте `Window > Package Manager`, выберите NetPulse, перейдите на вкладку `Samples` и импортируйте `Basic`.
2. Выберите `NetPulse > Samples > Build Basic Sample Scene`. Команда создаёт ассет настроек, если его нет, префаб игрока, реестр префабов и две сцены (`BasicSample` и `BasicSampleArena`) в `Assets/NetPulse Basic Sample`, и добавляет обе сцены в Build Settings.
3. Запустите сервер, откройте `BasicSample` и нажмите Play. Запустите второй экземпляр (отдельную сборку или виртуального игрока из Multiplayer Play Mode Unity), чтобы увидеть двух игроков.

Управление в примере: `WASD` двигает игрока, `Space` отправляет RPC, `H` уменьшает ваше здоровье на `SyncVar`, а `E` забирает общий куб, который затем двигается стрелками.

## Запуск тестов пакета

В пакете есть тесты в `Tests/Runtime` (сериализация, клиент на поддельном транспорте, сообщения, типизированные ключи, тики, async-помощники, сетевые сцены, статистика, `SyncVar`, сгенерированные RPC, хуки и владение) и в `Tests/Editor`. Они видны в Test Runner, когда пакет указан в `testables` файла `Packages/manifest.json`. Пакет, встроенный в `Packages`, уже доступен для тестирования.

Тесты владения создают GameObject и используют `[UnityTest]`, поэтому запускайте их из Test Runner в режиме Play Mode.

## Что дальше

Продолжайте со страницы [подключения](connecting.md), затем [подбор комнат](matchmaking.md). Если вы знаете Photon, страница [перехода с PUN](../migrating-from-pun.md) сопоставит известные вам понятия.
