# Развёртывание

Здесь описано, как собрать релизную папку, чем отличаются два вида сборки, как запустить сервер как службу и как устроена папка плагинов.

## Публикация

```bash
dotnet publish src/NetPulse.Server -c Release -o publish
dotnet publish plugins/NetPulse.Plugins.Sample -c Release -o publish/plugins/NetPulse.Plugins.Sample
```

Первая команда публикует сервер. Вторая публикует плагин в папку `plugins` внутри результата. Свои плагины публикуйте так же, по одной папке на плагин.

Итоговая папка выглядит так:

```text
publish/
  NetPulse.Server.exe        (NetPulse.Server on Linux)
  NetPulse.Server.dll
  NetPulse.Common.dll
  NetPulse.Plugins.dll
  LiteNetLib.dll
  appsettings.json
  plugins/
    NetPulse.Plugins.Sample/
      plugin.json
      NetPulse.Plugins.Sample.dll
      NetPulse.Plugins.Sample.pdb
```

Отредактируйте `appsettings.json` в папке публикации или оставьте его как есть и пользуйтесь переменными окружения. Помните, что хост читает `appsettings.json` из рабочей папки, см. [Запуск сервера](running.md#changing-settings).

## Зависимая от платформы или автономная сборка

=== "Зависимая от платформы"

    ```bash
    dotnet publish src/NetPulse.Server -c Release -o publish
    ```

    Около 3 МБ. На целевой машине должна быть среда выполнения .NET 10 (`Microsoft.NETCore.App`). ASP.NET Core Runtime не нужен. Обновления среды приходят через менеджер пакетов операционной системы.

=== "Автономная"

    ```bash
    dotnet publish src/NetPulse.Server -c Release -r linux-x64 --self-contained -o publish
    ```

    Укажите идентификатор среды выполнения целевой системы, например `win-x64`, `linux-x64` или `linux-arm64`. Результат содержит собственную среду выполнения и занимает около 80 МБ, а на целевой машине ничего устанавливать не нужно. Когда обновлять среду, вы решаете сами, публикуя сборку заново.

Плагины представляют собой обычные библиотеки классов, и они загружаются в обоих режимах. Публикуйте их без идентификатора среды выполнения: они работают на той среде, которую использует сервер.

## Откройте порт {#open-the-port}

Клиенты подключаются по UDP к `NetPulse:Port` (по умолчанию 5055). Разрешите входящий UDP на этом порту и в файрволе машины, и в облачной группе безопасности или роутере перед ней. Правила для TCP не помогут.

=== "Linux (ufw)"

    ```bash
    sudo ufw allow 5055/udp
    ```

=== "Linux (firewalld)"

    ```bash
    sudo firewall-cmd --permanent --add-port=5055/udp
    sudo firewall-cmd --reload
    ```

=== "Windows"

    ```powershell
    New-NetFirewallRule -DisplayName "NetPulse" -Direction Inbound -Protocol UDP -LocalPort 5055 -Action Allow
    ```

## Устройство папки плагинов

Сервер просматривает `NetPulse:Plugins:Directory` (по умолчанию папка `plugins` рядом с бинарником). Каждая вложенная папка содержит один плагин, и в ней нужен `plugin.json`:

```json
{
  "name": "MyPlugin",
  "assembly": "MyPlugin.dll",
  "factory": "MyNamespace.MyFactory"
}
```

| Поле | Значение |
|---|---|
| `name` | Имя, которое комнаты указывают в `RoomOptions.Plugin`, и ключ в `NetPulse:Plugins:Settings`. |
| `assembly` | Dll из той же папки, в которой находится плагин. |
| `factory` | Полное имя класса, реализующего `IPluginFactory`. Обязательно для плагинов комнат. |
| `auth` | Полное имя класса, реализующего `IAuthPlugin`. См. [Аутентификация](../plugins/authentication.md). |

Манифесту нужны `name`, `assembly` и хотя бы одно из полей `factory` или `auth`. Папка без `plugin.json` пропускается. Сервер никогда не берёт `NetPulse.Plugins.dll`, `NetPulse.Common.dll`, `Microsoft.Extensions.Logging.Abstractions.dll` и `Microsoft.Extensions.DependencyInjection.Abstractions.dll` из папки плагина: плагин всегда получает собственные копии сервера. Остальные зависимости плагина загружаются из его папки, поэтому публикуйте плагин и копируйте весь результат.

## Запуск как службы

Службе стандартный ввод не нужен, поэтому отключите консоль: задайте `NetPulse__EnableConsole=false`.

=== "Linux (systemd)"

    Создайте `/etc/systemd/system/netpulse.service`:

    ```ini
    [Unit]
    Description=NetPulse server
    After=network.target

    [Service]
    Type=simple
    User=netpulse
    WorkingDirectory=/opt/netpulse
    ExecStart=/opt/netpulse/NetPulse.Server
    Environment=NetPulse__EnableConsole=false
    Restart=on-failure
    RestartSec=5

    [Install]
    WantedBy=multi-user.target
    ```

    Затем включите службу:

    ```bash
    sudo systemctl daemon-reload
    sudo systemctl enable --now netpulse
    journalctl -u netpulse -f
    ```

    Сделайте бинарник исполняемым (`chmod +x /opt/netpulse/NetPulse.Server`). Для сборки, зависимой от платформы, задайте `ExecStart=/usr/bin/dotnet /opt/netpulse/NetPulse.Server.dll`. `WorkingDirectory` важен, потому что `appsettings.json` читается оттуда. Команда `systemctl stop` отправляет SIGTERM, что запускает корректную остановку, описанную в разделе [Запуск сервера](running.md#stopping-the-server).

=== "Windows"

    Сервер не интегрирован с диспетчером служб Windows, поэтому регистрация `NetPulse.Server.exe` напрямую через `sc.exe` не работает. Используйте обёртку для служб, например NSSM:

    ```powershell
    nssm install NetPulse "C:\NetPulse\NetPulse.Server.exe"
    nssm set NetPulse AppDirectory "C:\NetPulse"
    nssm set NetPulse AppEnvironmentExtra NetPulse__EnableConsole=false
    nssm start NetPulse
    ```

    Подойдёт и задача планировщика, которая запускается при старте системы с той же рабочей папкой.

=== "Контейнер"

    Опубликуйте сервер, скопируйте папку публикации в образ со средой выполнения .NET 10 (или используйте автономную сборку) и откройте порт как UDP:

    ```bash
    docker run -p 5055:5055/udp -e NetPulse__EnableConsole=false my-netpulse
    ```

    Без `/udp` Docker пробросит TCP, и ни один клиент не подключится.

## Обновление

- Только плагин: перезапишите файлы плагина на месте. Если включена `HotReload`, сервер загрузит новую версию для новых комнат, а работающие комнаты доработают на старой. См. [Горячая перезагрузка](../plugins/hot-reload.md).
- Бинарник сервера или конфигурация: остановите сервер, замените файлы и запустите снова. Клиенты в комнатах при остановке будут отключены от комнат, поэтому планируйте перезапуск.
