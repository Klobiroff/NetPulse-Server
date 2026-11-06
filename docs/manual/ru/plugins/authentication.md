# Аутентификация

По умолчанию сервер принимает любого клиента и доверяет присланным `UserId` и `NickName`. Плагин аутентификации это меняет: плагин сначала видит учётные данные клиента и решает, кого впускать и под какой идентичностью.

## IAuthPlugin

```csharp
public interface IAuthPlugin
{
    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct);
}
```

`AuthRequest` содержит то, что прислал клиент:

| Свойство | Описание |
|---|---|
| `AppId` | AppId из настроек клиента. |
| `UserId`, `NickName` | То, чем клиент себя называет. Не доверяйте им. |
| `AuthToken` | Токен, заданный клиентом в параметрах подключения. |
| `AuthData` | Необязательная `PropertyTable` с дополнительными значениями. |
| `RemoteEndpoint` | Адрес клиента в виде текста. |

Верните `AuthResult.Ok(userId, nickName)`, чтобы принять клиента, или `AuthResult.Rejected(error)`, чтобы отказать. Возвращённые `userId` и `nickName` заменяют клиентские значения до конца сессии. Значение `null` оставляет клиентское, а если оба в итоге пусты, сервер сам создаёт идентификатор пользователя и никнейм.

## Что сервер делает с ответом

- Сначала сервер проверяет AppId и версию протокола. Ваш плагин работает только для клиентов, которые их прошли.
- `Rejected`, тайм-аут (`NetPulse:Plugins:AuthTimeoutMs`, по умолчанию 5 секунд) или отменённый вызов дают клиенту `AuthenticationFailed` (-32) с вашим сообщением об ошибке. Примерно через 250 мс сервер отключает клиента.
- Исключение внутри плагина тоже приводит к `AuthenticationFailed` с общим сообщением.
- Уже аутентифицированный клиент, повторивший запрос, получает `Ok`.

Аутентификация глобальная, а не для отдельной комнаты. Настраивается она параметром `NetPulse:Plugins:Auth` с именем плагина. Если этот плагин не загрузился, сервер отказывается запускаться. См. [Безопасность](../server/security.md).

## Упаковка

В `plugin.json` плагина аутентификации тип указывается в `auth`. У плагина может быть только `auth`, только `factory` или оба поля:

```json
{
  "name": "DevAuth",
  "assembly": "DevAuth.dll",
  "auth": "AuthExamples.DevTokenAuth"
}
```

Сервер создаёт объект аутентификации один раз на загрузку и только для плагина, чьё имя указано в `NetPulse:Plugins:Auth`. Конструктор он ищет в таком порядке:

1. `(IReadOnlyDictionary<string, string> config, IAuthPluginContext context)`
2. `(IReadOnlyDictionary<string, string> config)`
3. Конструктор без параметров.

В `config` лежат значения `NetPulse:Plugins:Settings:<Plugin>` этого плагина. `IAuthPluginContext` даёт `Logger` и `HttpAsync(request, ct)`, который отправляет запрос через фабрику HTTP-клиентов сервера. Чтобы отказаться от запуска с неверной конфигурацией, бросьте исключение из конструктора.

Небольшой плагин для серверов разработки, где токены перечислены в настройках:

```csharp title="DevTokenAuth.cs"
using Microsoft.Extensions.Logging;
using NetPulse.Plugins;

namespace AuthExamples;

public sealed class DevTokenAuth : IAuthPlugin
{
    private readonly Dictionary<string, string> _users = new();
    private readonly IAuthPluginContext _context;

    public DevTokenAuth(IReadOnlyDictionary<string, string> config, IAuthPluginContext context)
    {
        _context = context;

        if (!config.TryGetValue("Tokens", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("DevAuth requires a Tokens setting like 'token1=alice,token2=bob'");
        }

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2);

            if (parts.Length == 2)
            {
                _users[parts[0]] = parts[1];
            }
        }
    }

    public Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct)
    {
        if (request.AuthToken is not null && _users.TryGetValue(request.AuthToken, out var name))
        {
            return Task.FromResult(AuthResult.Ok(name, name));
        }

        _context.Logger.LogWarning("Rejected a login from {Endpoint}", request.RemoteEndpoint);
        return Task.FromResult(AuthResult.Rejected("Unknown token"));
    }
}
```

Включите его так:

```json
{
  "NetPulse": {
    "Plugins": {
      "Auth": "DevAuth",
      "Settings": {
        "DevAuth": {
          "Tokens": "token1=alice,token2=bob"
        }
      }
    }
  }
}
```

## Плагин AuthSample

`plugins/NetPulse.Plugins.AuthSample` готов к использованию. Имя плагина: `AuthSample`. Пока вы не зададите `NetPulse:Plugins:Auth` равным `AuthSample`, он выключен. Соберите решение (это копирует его рядом с сервером) или опубликуйте его в папку `plugins`:

```bash
dotnet publish plugins/NetPulse.Plugins.AuthSample -c Release -o publish/plugins/AuthSample
```

Настройки задаются в `NetPulse:Plugins:Settings:AuthSample`:

| Ключ | По умолчанию | Значение |
|---|---|---|
| `Mode` | `Hmac` | `Hmac` или `Http`. Любое другое значение не даст серверу запуститься. |
| `Secret` | нет | Обязателен в режиме `Hmac`. Секрет для подписи. |
| `Url` | нет | Обязателен в режиме `Http`. Абсолютный URL. |

### Режим Hmac

Плагин проверяет токены автономно, без сетевых вызовов. Токен состоит из двух частей в base64url, соединённых точкой:

```text
base64url(userId|nickName|expiryUnixSeconds) . base64url(HMACSHA256(secret, payloadBytes))
```

Сервер проверяет подпись за постоянное время, затем срок действия. Неверный токен даёт `Invalid token`, просроченный даёт `Token expired`. Идентификатор пользователя и никнейм не должны содержать `|`. Секрет не пишется в лог.

```json
{
  "NetPulse": {
    "Plugins": {
      "Auth": "AuthSample",
      "Settings": {
        "AuthSample": {
          "Mode": "Hmac",
          "Secret": "change-me-long-random-string"
        }
      }
    }
  }
}
```

Работают и переменные окружения: `NetPulse__Plugins__Auth=AuthSample`, `NetPulse__Plugins__Settings__AuthSample__Mode=Hmac` и `NetPulse__Plugins__Settings__AuthSample__Secret=...`.

Токены создаёт ваш бэкенд по тому же рецепту (см. `HmacTokens.Create` в исходниках плагина). Для проверки токен печатает тестовый клиент:

```bash
dotnet run --project tools/NetPulse.TestClient -- --make-token --secret change-me-long-random-string --user user42 --nick Alice --ttl-minutes 60
```

| Флаг | По умолчанию | Значение |
|---|---|---|
| `--make-token` | | Напечатать токен и выйти. |
| `--secret` | нет | Обязателен. Тот же секрет, что и на сервере. |
| `--user` | `user1` | Идентификатор пользователя внутри токена. |
| `--nick` | `Player` | Никнейм внутри токена. |
| `--ttl-minutes` | `60` | Срок жизни токена. |

После этого подключите бота с токеном:

```bash
dotnet run --project tools/NetPulse.TestClient -- --bots 1 --token <token> --plugin ""
```

Бот напечатает `bot1 joined 'test' as actor 1`, а сервер запишет `Creating room test for user42`: идентичность пришла из токена. Без `--token` бот получает `authenticate failed (-32 Invalid token)`. (С `--plugin ""` итоговая самопроверка бота сообщает `FAILED`, потому что тестовое событие бота некому обработать. См. [Запуск сервера](../server/running.md#try-it-with-the-test-client).)

### Режим Http

Плагин отправляет вход вашему сервису. Задайте `Mode` равным `Http`, а `Url` адресом обработчика:

```json
{
  "Mode": "Http",
  "Url": "https://auth.example/verify"
}
```

Для каждого входа сервер отправляет POST с JSON-телом в camelCase, собранным из запроса: `appId`, `userId`, `nickName`, `authToken`, `authData` и `remoteEndpoint`. Ваш сервис отвечает JSON:

```json
{ "ok": true, "userId": "user42", "nickName": "Alice", "message": "", "data": {} }
```

`ok: false` отклоняет клиента, а `message` становится текстом ошибки. Статус не 2xx, недоступный сервис или некорректное тело ответа тоже отклоняют клиента. Вызов учитывает `AuthTimeoutMs`.

## Unity-клиент

Передайте токен в параметрах подключения:

```csharp
await Pulse.Connect(new ConnectionOptions
{
    Host = "play.example.com",
    AuthToken = token
});
```

Ответ сервера перекрывает `UserId` и `NickName`, присланные клиентом, поэтому идентичность, которую другие игроки видят в комнате, берётся из вашего плагина. См. [Подключение](../unity/connecting.md).

## Горячая перезагрузка

Замена файлов плагина аутентификации перезагружает его, и новые входы используют новую версию. Если перезагрузка не удалась, продолжает работать прежняя версия.
