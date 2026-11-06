# Написание плагина

Это пошаговое руководство собирает небольшой плагин от пустого проекта до загруженного сервером. Плагин приветствует каждого входящего игрока и маскирует запрещённые слова в сообщениях чата.

## 1. Создайте проект

Понадобятся .NET 10 SDK и клон репозитория сервера: плагин ссылается на его проекты `NetPulse.Plugins` и `NetPulse.Common`.

```bash
dotnet new classlib -n GreeterPlugin -f net10.0
cd GreeterPlugin
rm Class1.cs
```

Замените содержимое `GreeterPlugin.csproj`. Путь `path\to\NetPulse_Server` поменяйте на расположение вашего клона:

```xml title="GreeterPlugin.csproj"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="path\to\NetPulse_Server\src\NetPulse.Plugins\NetPulse.Plugins.csproj" Private="false" />
    <ProjectReference Include="path\to\NetPulse_Server\src\NetPulse.Common\NetPulse.Common.csproj" Private="false" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.14" PrivateAssets="all" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <None Include="plugin.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`Private="false"` не пускает сборки SDK в ваш результат. Хост всегда подставляет собственные копии `NetPulse.Plugins`, `NetPulse.Common`, `Microsoft.Extensions.Logging.Abstractions` и `Microsoft.Extensions.DependencyInjection.Abstractions` и игнорирует копии из папки плагина. Пакет логирования подключён только для того, чтобы плагин компилировался.

Если на проекты сослаться нельзя, один раз соберите репозиторий и сошлитесь на dll:

```xml
<ItemGroup>
  <Reference Include="NetPulse.Plugins">
    <HintPath>path\to\NetPulse_Server\src\NetPulse.Plugins\bin\Release\net10.0\NetPulse.Plugins.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="NetPulse.Common">
    <HintPath>path\to\NetPulse_Server\src\NetPulse.Common\bin\Release\netstandard2.1\NetPulse.Common.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

## 2. Напишите фабрику и плагин

В плагине два класса. Фабрика читает конфигурацию и создаёт по одному объекту плагина на комнату. Плагин наследуется от `RoomPlugin` и переопределяет нужные хуки.

```csharp title="GreeterPlugin.cs"
using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;

namespace GreeterPlugin;

public sealed class GreeterFactory : IPluginFactory
{
    public ValueTask<PluginCreateResult> CreateAsync(IPluginHost host, string pluginName, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        var greeting = config.TryGetValue("Greeting", out var value) ? value : "Welcome";
        var banned = config.TryGetValue("BannedWords", out var raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        return ValueTask.FromResult(PluginCreateResult.Ok(new GreeterPlugin(host, greeting, banned)));
    }
}

public sealed class GreeterPlugin : RoomPlugin
{
    public const byte ChatEvent = 20;
    public const byte GreetingEvent = 60;

    private readonly string _greeting;
    private readonly string[] _banned;

    public GreeterPlugin(IPluginHost host, string greeting, string[] banned)
        : base(host)
    {
        _greeting = greeting;
        _banned = banned;
    }

    public override ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct)
    {
        var actorId = info.Actor.ActorId;
        var text = $"{_greeting}, {info.Actor.NickName}!";

        Host.Enqueue(() => Host.BroadcastEvent(
            GreetingEvent,
            text,
            ReceiverGroup.Targets,
            new[]
            {
                actorId
            }));

        Host.Logger.LogInformation("Greeted {Nick} in room {Room}", info.Actor.NickName, Host.RoomName);
        info.Continue();
        return default;
    }

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        if (info.Request.EventCode == ChatEvent && info.Request.Data is string message)
        {
            info.Request.Data = Mask(message);
        }

        info.Continue();
        return default;
    }

    private string Mask(string message)
    {
        foreach (var word in _banned)
        {
            message = message.Replace(word, new string('*', word.Length), StringComparison.OrdinalIgnoreCase);
        }

        return message;
    }
}
```

Что здесь происходит:

- `CreateAsync` возвращает `PluginCreateResult`. Чтобы отказаться создавать плагин комнаты, верните `PluginCreateResult.Failed("причина")`: клиент получит `PluginReportedError`.
- `OnJoinAsync` вызывается после того, как игрок добавлен в комнату, но до того, как сервер отправил ответ на вход. Сообщение, отправленное из хука, дошло бы до клиента раньше, чем он узнает, что находится в комнате, поэтому плагин отправляет приветствие через `Host.Enqueue`, когда вход уже завершён.
- Каждый хук обязан вызвать `Continue()`, `Fail(message)` или, где это предусмотрено, `Cancel()` до возврата. Хук, вернувшийся без решения, попадает в лог как ошибка и считается `Fail`.
- `info.Request` можно изменять. Если поменять `Data` в `OnRaiseEventAsync`, другие игроки получат изменённое значение.
- Хуки представляют собой методы с `ValueTask`. Если ждать нечего, возвращайте `default`.

## 3. Добавьте манифест

Создайте `plugin.json` рядом с файлом проекта. Csproj копирует его в результат.

```json title="plugin.json"
{
  "name": "GreeterPlugin",
  "assembly": "GreeterPlugin.dll",
  "factory": "GreeterPlugin.GreeterFactory"
}
```

В `name` указано имя, которое используют комнаты и конфигурация. В `factory` указано полное имя класса фабрики вместе с пространством имён.

## 4. Соберите и установите

```bash
dotnet publish -c Release -o out
```

Скопируйте результат в папку с именем плагина внутри каталога плагинов сервера:

=== "Запуск из исходников"

    ```bash
    mkdir -p path/to/NetPulse_Server/src/NetPulse.Server/bin/Debug/net10.0/plugins/GreeterPlugin
    cp out/GreeterPlugin.dll out/GreeterPlugin.pdb out/plugin.json path/to/NetPulse_Server/src/NetPulse.Server/bin/Debug/net10.0/plugins/GreeterPlugin/
    ```

=== "Опубликованный сервер"

    ```bash
    mkdir -p publish/plugins/GreeterPlugin
    cp out/GreeterPlugin.dll out/GreeterPlugin.pdb out/plugin.json publish/plugins/GreeterPlugin/
    ```

Если сервер уже запущен с горячей перезагрузкой, он подхватит новую папку сам в течение секунды.

## 5. Включите плагин

Комнаты используют плагин, если его имя указано в `RoomOptions.Plugin` или если он плагин по умолчанию. Чтобы сделать его плагином по умолчанию и передать настройки, добавьте в `appsettings.json`:

```json
{
  "NetPulse": {
    "Plugins": {
      "Default": "GreeterPlugin",
      "Settings": {
        "GreeterPlugin": {
          "Greeting": "Hello",
          "BannedWords": "darn,heck"
        }
      }
    }
  }
}
```

Подойдут и переменные окружения: `NetPulse__Plugins__Default=GreeterPlugin`.

Из Unity плагин может запросить отдельная комната:

```csharp
var room = await Pulse.JoinOrCreateRoom("lobby-1", new RoomOptions { MaxPlayers = 8, Plugin = "GreeterPlugin" });
```

## 6. Убедитесь, что плагин загрузился

Запустите сервер и найдите сообщение о загрузке:

```text
info: NetPulse.Server.Plugins.PluginManager[0] Loaded plugin GreeterPlugin v2 from GreeterPlugin
```

Выполните в консоли сервера `plugins`, чтобы увидеть его в списке. Затем войдите в комнату. В логе сервера появится `Greeted Alice in room lobby-1`, а вошедший клиент получит событие 60 с текстом приветствия. Сообщения чата, отправленные как событие 20, дойдут до других игроков с замаскированными запрещёнными словами.

Если плагин не загрузился, смотрите список проверок в разделе [Эксплуатация](../server/operations.md#a-plugin-is-not-found-or-fails). Как проверять хуки без игрового клиента, описано в разделе [Тестирование](testing.md).

## Дальше

- [Хуки](hooks.md) описывают каждый хук и правила для Continue, Fail и Cancel.
- [API хоста](host-api.md) перечисляет всё, что плагин может сделать со своей комнатой.
- [Таймеры, HTTP и фоновая работа](timers-http-background.md) рассказывают о работе вне хуков.
