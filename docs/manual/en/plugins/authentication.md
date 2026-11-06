# Authentication

By default the server accepts any client and trusts the `UserId` and `NickName` it sends. An authentication plugin changes that: the plugin sees the client's credentials first and decides who gets in and under what identity.

## IAuthPlugin

```csharp
public interface IAuthPlugin
{
    Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct);
}
```

`AuthRequest` carries what the client sent:

| Property | Description |
|---|---|
| `AppId` | The AppId from the client settings. |
| `UserId`, `NickName` | What the client claims. Do not trust them. |
| `AuthToken` | The token the client set in its connection options. |
| `AuthData` | An optional `PropertyTable` with extra values. |
| `RemoteEndpoint` | The client's address as text. |

Return `AuthResult.Ok(userId, nickName)` to accept or `AuthResult.Rejected(error)` to refuse. The `userId` and `nickName` you return replace the client's values for the rest of the session. A `null` value keeps the client's value, and if both end up empty the server generates a user id and a nickname.

## What the server does with the answer

- The server checks the AppId and protocol version first. Your plugin runs only for clients that pass them.
- `Rejected`, a timeout (`NetPulse:Plugins:AuthTimeoutMs`, 5 seconds by default) or a cancelled call give the client `AuthenticationFailed` (-32) with your error message. The server disconnects the client about 250 ms later.
- An exception inside the plugin also answers `AuthenticationFailed` with a generic message.
- A client that is already authenticated gets `Ok` if it sends the request again.

Authentication is global, not per room. Configure it with `NetPulse:Plugins:Auth`, the name of the plugin. If that plugin does not load, the server refuses to start. See [Security](../server/security.md).

## Packaging

The `plugin.json` of an authentication plugin names the type in `auth`. A plugin may have only `auth`, only `factory`, or both:

```json
{
  "name": "DevAuth",
  "assembly": "DevAuth.dll",
  "auth": "AuthExamples.DevTokenAuth"
}
```

The server creates the auth object once per load, and only for the plugin named in `NetPulse:Plugins:Auth`. It looks for a constructor in this order:

1. `(IReadOnlyDictionary<string, string> config, IAuthPluginContext context)`
2. `(IReadOnlyDictionary<string, string> config)`
3. A parameterless constructor.

`config` holds the plugin's `NetPulse:Plugins:Settings:<Plugin>` values. `IAuthPluginContext` offers a `Logger` and `HttpAsync(request, ct)`, which sends through the server's HTTP client factory. Throw from the constructor to refuse to start with a bad configuration.

A small plugin for development servers, with tokens listed in the settings:

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

Enable it with:

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

## The AuthSample plugin

`plugins/NetPulse.Plugins.AuthSample` is ready to use. Its plugin name is `AuthSample`. It is off until you set `NetPulse:Plugins:Auth` to `AuthSample`. Build the solution (which copies it next to the server) or publish it into your `plugins` folder:

```bash
dotnet publish plugins/NetPulse.Plugins.AuthSample -c Release -o publish/plugins/AuthSample
```

Settings go under `NetPulse:Plugins:Settings:AuthSample`:

| Key | Default | Meaning |
|---|---|---|
| `Mode` | `Hmac` | `Hmac` or `Http`. Anything else stops the server from starting. |
| `Secret` | none | Required in `Hmac` mode. The signing secret. |
| `Url` | none | Required in `Http` mode. An absolute URL. |

### Hmac mode

The plugin verifies tokens offline, with no network call. A token is two base64url parts joined by a dot:

```text
base64url(userId|nickName|expiryUnixSeconds) . base64url(HMACSHA256(secret, payloadBytes))
```

The server checks the signature in constant time, then the expiry. A bad token gives `Invalid token`, an old one `Token expired`. The user id and nickname must not contain `|`. The secret is never logged.

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

Environment variables work too: `NetPulse__Plugins__Auth=AuthSample`, `NetPulse__Plugins__Settings__AuthSample__Mode=Hmac` and `NetPulse__Plugins__Settings__AuthSample__Secret=...`.

Your backend creates tokens with the same recipe (see `HmacTokens.Create` in the plugin source). For testing, the test client prints one:

```bash
dotnet run --project tools/NetPulse.TestClient -- --make-token --secret change-me-long-random-string --user user42 --nick Alice --ttl-minutes 60
```

| Flag | Default | Meaning |
|---|---|---|
| `--make-token` | | Print a token and exit. |
| `--secret` | none | Required. The same secret as the server. |
| `--user` | `user1` | User id inside the token. |
| `--nick` | `Player` | Nickname inside the token. |
| `--ttl-minutes` | `60` | Lifetime of the token. |

Then connect a bot with it:

```bash
dotnet run --project tools/NetPulse.TestClient -- --bots 1 --token <token> --plugin ""
```

The bot prints `bot1 joined 'test' as actor 1`, and the server logs `Creating room test for user42`: the identity came from the token. Without `--token` the bot fails with `authenticate failed (-32 Invalid token)`. (With `--plugin ""` the bot's closing self-check reports `FAILED` because no sample plugin answers its test event. See [Running the server](../server/running.md#try-it-with-the-test-client).)

### Http mode

The plugin sends the login to your service. Set `Mode` to `Http` and `Url` to the endpoint:

```json
{
  "Mode": "Http",
  "Url": "https://auth.example/verify"
}
```

For each login the server sends a POST with a camelCase JSON body made from the request: `appId`, `userId`, `nickName`, `authToken`, `authData` and `remoteEndpoint`. Your service answers with JSON:

```json
{ "ok": true, "userId": "user42", "nickName": "Alice", "message": "", "data": {} }
```

`ok: false` rejects the client and `message` becomes the error. A non-2xx status, an unreachable service or a malformed body also reject the client. The call honours `AuthTimeoutMs`.

## Unity client

Pass the token in the connection options:

```csharp
await Pulse.Connect(new ConnectionOptions
{
    Host = "play.example.com",
    AuthToken = token
});
```

The server's answer overrides the `UserId` and `NickName` the client sent, so the identity other players see in the room comes from your plugin. See [Connecting](../unity/connecting.md).

## Hot reload

Replacing the files of the auth plugin reloads it, and new logins use the new version. If the reload fails, the previous version keeps working.
