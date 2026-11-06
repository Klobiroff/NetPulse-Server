# Быстрый старт

Эта страница ведёт от пустого проекта к Unity-клиенту, который сидит в комнате на вашем собственном сервере. Каждый шаг ссылается на подробные страницы.

## Что понадобится

- .NET 10 SDK, чтобы собрать и запустить сервер.
- Unity 2021.3 или новее (6000.x тоже подходит) для клиента.
- Доступ по git к репозиториям NetPulse. Пока они закрытые.

## 1. Запустите сервер

Клонируйте репозиторий сервера, соберите и запустите его:

```bash
git clone https://github.com/Klobiroff/NetPulse-Server.git
cd NetPulse-Server
dotnet build
dotnet run --project src/NetPulse.Server
```

Сборка заодно копирует пример плагина в папку `plugins` сервера. Сервер пишет в лог порт, который он слушает:

```text
info: NetPulse.Server.Plugins.PluginManager[0] Loaded plugin NetPulse.Plugins.Sample v1 from NetPulse.Plugins.Sample
info: NetPulse.Server.Networking.NetPulseServer[0] NetPulse server listening on UDP 5055
info: NetPulse.Server.Commands.ConsoleCommandService[0] Console commands: stats, plugins, reload <folder>, quit
```

Порт по умолчанию: UDP 5055. Если между сервером и клиентами стоит файрвол, откройте этот UDP-порт. Подробности в разделе [Запуск сервера](server/running.md).

Чтобы проверить сервер, не открывая Unity, запустите двух тестовых ботов во втором терминале:

```bash
dotnet run --project tools/NetPulse.TestClient -- --bots 2 --room test --seconds 6
```

Боты входят в комнату `test`, обмениваются событиями и в конце печатают `OK`, если всё прошло успешно.

## 2. Установите Unity-пакет

Пакет лежит в репозитории Unity в папке `Packages/com.netpulse.unity`. Откройте **Window > Package Manager**, нажмите **+**, выберите **Add package from git URL** и вставьте:

```text
https://github.com/Klobiroff/NetPulse-Unity.git?path=Packages/com.netpulse.unity
```

Git запускает сам Unity, поэтому на машине должен быть установлен git, а также учётные данные с правом чтения закрытого репозитория.

!!! note "Нет доступа к git?"
    Скопируйте папку `Packages/com.netpulse.unity` из клона репозитория Unity в папку `Packages` вашего проекта. Unity подхватит её при следующей перезагрузке доменов. Можно также выбрать **Add package from disk** и указать `package.json` внутри этой папки.

Подробнее на странице [Установка](unity/installation.md).

## 3. Настройте подключение

Откройте **Project Settings > NetPulse** (или **NetPulse > Create Settings Asset**). Unity создаст `Assets/Resources/NetPulseSettings.asset`. Укажите хост и порт сервера. Оставьте AppId пустым, если не задавали `NetPulse:AppId` на сервере. См. [Настройки и редактор](unity/settings-and-editor.md).

## 4. Подключитесь и войдите в комнату

Добавьте этот скрипт на любой GameObject в сцене:

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class Demo : MonoBehaviour
{
    private async void Start()
    {
        Pulse.NickName = "Alice";
        await Pulse.Connect();
        var room = await Pulse.JoinOrCreateRoom("arena", new RoomOptions { MaxPlayers = 4 });
        Debug.Log($"Joined {room.Name}, players: {room.PlayerCount}");
    }
}
```

Нажмите Play. В логе сервера для первого игрока появится строка `Creating room arena for ...`. Запустите вторую копию (сборку или экземпляр Multiplayer Play Mode), и число игроков вырастет.

Неудавшаяся операция бросает `NetPulseException` с `ReturnCode` от сервера. Коды перечислены в [Коды и ключи](reference/codes.md).

## Что читать дальше

- [Основные понятия](concepts.md) объясняют комнаты, пиры, сетевые объекты и владение.
- [Подключение](unity/connecting.md) и [Подбор комнат](unity/matchmaking.md) описывают лобби и разные способы входа.
- [Сетевые объекты](unity/network-objects.md) и [RPC](unity/rpc.md) отвечают за синхронизацию геймплея.
- [Конфигурация](server/configuration.md) перечисляет все настройки сервера, а [Написание плагина](plugins/writing-a-plugin.md) показывает, как добавить серверную логику.
