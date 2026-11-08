# Connecting

`Pulse` is the static entry point of the client. (It is not called `NetPulse` because `NetPulse` is also the namespace, and C# cannot have a type and a namespace with the same name visible from user code.) Connecting takes one call, and the call returns an operation you can `await`.

```csharp
using NetPulse;
using UnityEngine;

public sealed class ConnectOnStart : MonoBehaviour
{
    private async void Start()
    {
        Pulse.NickName = "Alice";
        Pulse.StateChanged += state => Debug.Log($"State: {state}");

        try
        {
            await Pulse.Connect();
            Debug.Log($"Connected as {Pulse.UserId}, ping {Pulse.RoundTripTimeMs} ms");
        }
        catch (NetPulseException e)
        {
            Debug.LogError($"Connect failed ({e.ReturnCode}): {e.Message}");
        }
    }
}
```

`Pulse.Connect()` reads `NetPulseSettings` (see [settings and editor](settings-and-editor.md)), opens the UDP connection and authenticates. It completes when the state reaches `Connected`. If you do not set `Pulse.NickName`, the client uses the `Default Nick Name` from the settings, and `Player` followed by a random number if that is empty too.

The operation can also be used from a coroutine or with a callback. The [async](async.md) page covers `NetOperation` in full.

```csharp
using System.Collections;
using NetPulse;
using UnityEngine;

public sealed class ConnectInCoroutine : MonoBehaviour
{
    private IEnumerator Start()
    {
        var operation = Pulse.Connect();
        yield return operation;

        if (operation.HasError)
        {
            Debug.LogError(operation.Error.ToString());
        }
    }
}
```

## Connection settings

`Pulse.Connect()` uses the settings asset in `Resources`. Pass your own `NetPulseSettings` to override it for this and later calls:

```csharp
using NetPulse;
using UnityEngine;

public sealed class ConnectWithSettings : MonoBehaviour
{
    private async void Start()
    {
        var settings = ScriptableObject.CreateInstance<NetPulseSettings>();
        settings.Host = "game.example.com";
        settings.Port = 5055;
        settings.AppId = "my-game";
        settings.AppVersion = "1.2";
        await Pulse.Connect(settings);
    }
}
```

`Pulse.Connect(settings)` also becomes the active `NetPulseSettings.Instance`, so rates, the prefab registry and the other fields come from it. A third overload takes a `ConnectionOptions` object with `Host`, `Port`, `ConnectionKey`, `AppId`, `AppVersion`, `UserId`, `NickName`, `AuthToken`, `RequestTimeout` and the [encryption](#encryption) fields. It skips the settings asset entirely, which suits tests and tools:

```csharp
using NetPulse;
using UnityEngine;

public sealed class ConnectWithOptions : MonoBehaviour
{
    private async void Start()
    {
        var options = new ConnectionOptions
        {
            Host = "127.0.0.1",
            Port = 5055,
            AppId = "my-game",
            AppVersion = "1.2",
            NickName = "Bot"
        };
        await Pulse.Connect(options);
    }
}
```

`ConnectionKey` is a shared secret that the UDP handshake checks. It defaults to `NetPulse` and must equal the key the server is configured with.

The settings can also turn on `Run In Background`: when it is on, `Pulse.Connect` sets `Application.runInBackground` so the connection survives a lost window focus. It never switches the option off.

## Identity

The client sends a `UserId` with the handshake. By default it is a random id generated once and kept in `PlayerPrefs` under `NetPulse.UserId`, so the same install is recognized across sessions. With `Persist User Id` off, every `Connect` uses a fresh id. When an authentication plugin is active, the server's answer decides the final `UserId` and `NickName`, and the client adopts them (`Pulse.UserId`, `Pulse.Client.NickName`).

## Authentication token

By default the server accepts any client. If it runs an authentication plugin (see [authentication](../plugins/authentication.md)), pass the token before connecting:

```csharp
using NetPulse;
using UnityEngine;

public sealed class ConnectWithToken : MonoBehaviour
{
    [SerializeField] private string _token;

    private async void Start()
    {
        Pulse.AuthToken = _token;
        await Pulse.Connect();
    }
}
```

A rejected token fails the `Connect` operation with `AuthenticationFailed` (-32), and the server drops the connection. The token is cleared when the domain reloads or play mode restarts, so set it on every run.

## Encryption

NetPulse can encrypt and authenticate the whole connection. The server decides what it accepts ([Security](../server/security.md#transport-encryption)), the client decides what it sends:

| Client mode | Behaviour |
|---|---|
| `Off` (default) | The client sends plain packets. It connects to a server whose mode is `Disabled` or `Optional`. |
| `Required` | The client never sends and never accepts a plain packet. It connects to a server whose mode is `Optional` or `Required`. |

There is no "prefer encryption" mode on purpose: a client that falls back to plain text can be pushed into it by an attacker.

To turn it on:

1. Run `NetPulse.Server keygen` on the server. It prints the **public** key. The private key stays on the server.
2. Open `Project Settings > NetPulse` (or the settings asset) and set `Encryption Mode` to `Required`.
3. Paste the public key into `Server Public Keys`. The field checks the format. Add a second key only while you rotate the server key ([Deployment](../server/deployment.md#rotating-the-server-key)).
4. Optionally fill `Pre Shared Key` if the server uses one.

The same fields exist in code:

```csharp
using NetPulse;
using NetPulse.Common.Security;
using UnityEngine;

public sealed class ConnectEncrypted : MonoBehaviour
{
    [SerializeField] private string _serverPublicKey = string.Empty;

    private async void Start()
    {
        var options = ConnectionOptions.From(NetPulseSettings.Instance, Pulse.UserId, "Player");
        options.EncryptionMode = ClientEncryptionMode.Required;
        options.ServerPublicKeys = new[] { _serverPublicKey };
        await Pulse.Connect(options);
        Debug.Log($"Encrypted: {Pulse.Client.IsEncrypted}");
    }
}
```

`ConnectionOptions.ValidateSecurity()` returns a message for a missing or malformed key, or `null` when the options are fine. `Pulse.Connect` fails the operation with `NetPulseReturnCode.InvalidConfiguration` (-104) in that case and sends nothing. `Pulse.Client.IsEncrypted` is `true` once the handshake has finished.

If the handshake cannot finish, the connection ends with one of two disconnect reasons: `SecureHandshakeTimeout` (the server did not answer within `Handshake Timeout`, which also happens with a wrong server key or pre-shared key, because the server stays silent on purpose) and `SecureHandshakeFailed` (the answer could not be verified, or the session was closed because it reached its counter limit). A key that the server does not know looks like a timeout, not like a rejection.

The server public key is not secret, but it has to be the real one: it is what protects the client from a man in the middle. The pre-shared key and the connection key travel inside the build and can be extracted, see the threat model in [Security](../server/security.md#what-is-protected-and-what-is-not).

Encryption adds 26 bytes to every datagram (more with `Padding Block`). The cryptography is C# code in the package and works with Mono and IL2CPP on every platform that supports `System.Net.Sockets`. WebGL, which has no UDP, is outside the scope of this transport.

## States and events

`Pulse.State` is a `NetState`. `Pulse.IsConnected` is true in every state except `Disconnected`, `Connecting` and `Authenticating`, so it also stays true while you are in a lobby or a room.

| State | When |
|---|---|
| `Disconnected` | Start state; after `Disconnect()`, a failed connect or a lost connection |
| `Connecting` | After `Connect()`, until the UDP link is up |
| `Authenticating` | The link is up and the server is checking the `AppId`, the protocol version and the token |
| `Connected` | Authenticated, outside any lobby or room |
| `InLobby` | After `JoinLobby()` |
| `JoiningRoom` | While a create or join request is pending |
| `InRoom` | After a successful create or join |
| `Leaving` | While `LeaveRoom()` is pending |

`NetState.Disconnecting` is declared for later use and is not set by the current client. `Connect()` while the state is not `Disconnected` fails at once with `InvalidState`.

Every notification exists in three equivalent forms. Use whichever fits the script:

1. C# events on `Pulse.Events`, for example `Pulse.Events.OnJoinedRoom += Handler;`.
2. A `MonoBehaviour` that derives from `NetPulseCallbacks`. It registers itself in `OnEnable`, unregisters in `OnDisable`, and you override only the methods you need.
3. Any object that implements one or more callback interfaces and is registered with `Pulse.AddCallbackTarget(this)` and `Pulse.RemoveCallbackTarget(this)`.

```csharp
using System.Collections.Generic;
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class NetworkLog : NetPulseCallbacks
{
    public override void OnConnected() => Debug.Log("Connected");

    public override void OnDisconnected(string cause) => Debug.Log($"Disconnected: {cause}");

    public override void OnRoomListUpdate(IReadOnlyList<RoomInfo> rooms) => Debug.Log($"{rooms.Count} rooms");
}
```

```csharp
using NetPulse;
using UnityEngine;

public sealed class RoomWatcher : MonoBehaviour, IRoomCallbacks
{
    private void OnEnable() => Pulse.AddCallbackTarget(this);

    private void OnDisable() => Pulse.RemoveCallbackTarget(this);

    public void OnPlayerEnteredRoom(Player player) => Debug.Log($"{player.NickName} entered");

    public void OnPlayerLeftRoom(Player player) => Debug.Log($"{player.NickName} left");

    public void OnRoomPropertiesChanged(NetPulse.Common.PropertyTable changed)
    {
    }

    public void OnPlayerPropertiesChanged(Player player, NetPulse.Common.PropertyTable changed)
    {
    }

    public void OnMasterClientSwitched(Player newMaster) => Debug.Log($"New master: {newMaster.NickName}");
}
```

Callbacks run on the main thread. An exception in one handler is logged and the others still run. The full list:

| Event on `Pulse.Events` | Interface | Arguments |
|---|---|---|
| `OnStateChanged` | none (use `Pulse.StateChanged`) | `NetState` |
| `OnConnected` | `INetPulseConnectionCallbacks` | none |
| `OnDisconnected` | `INetPulseConnectionCallbacks` | `string cause` |
| `OnJoinedLobby`, `OnLeftLobby` | `ILobbyCallbacks` | none |
| `OnRoomListUpdate` | `ILobbyCallbacks` | `IReadOnlyList<RoomInfo>` |
| `OnCreatedRoom`, `OnJoinedRoom`, `OnLeftRoom` | `IMatchmakingCallbacks` | none |
| `OnCreateRoomFailed`, `OnJoinRoomFailed`, `OnJoinRandomFailed` | `IMatchmakingCallbacks` | `short returnCode, string message` |
| `OnPlayerEnteredRoom`, `OnPlayerLeftRoom` | `IRoomCallbacks` | `Player` |
| `OnRoomPropertiesChanged` | `IRoomCallbacks` | `PropertyTable changed` |
| `OnPlayerPropertiesChanged` | `IRoomCallbacks` | `Player, PropertyTable changed` |
| `OnMasterClientSwitched` | `IRoomCallbacks` | `Player newMaster` |
| `OnOwnershipRequest` | `IOwnershipCallbacks` | `NetworkObject, Player requester` |
| `OnOwnershipChanged` | `IOwnershipCallbacks` | `NetworkObject, Player previous, Player current` |
| `OnOwnershipFailed` | `IOwnershipCallbacks` | `NetworkObject, short returnCode, string message` |
| `OnNetworkSceneLoaded` | `ISceneCallbacks` | `string sceneName` |
| `OnEvent` | `IOnEventCallback` | `byte code, int senderActorId, object data` |

`NetPulseCallbacks` implements all of the interfaces except that its methods are virtual and empty. `OnEvent` carries your own event codes 0 to 199 only (see the [low-level API](low-level-api.md)).

## Disconnecting and disconnect reasons

`Pulse.Disconnect()` closes the connection from your side. The `OnDisconnected` event (and the failed operations) carry a reason string:

| Reason | Cause |
|---|---|
| `Client disconnect` | You called `Pulse.Disconnect()` or the application quit |
| `Timeout` | No packets arrived for 10 seconds |
| `ConnectionFailed` | The handshake got no answer |
| `HostUnreachable`, `NetworkUnreachable`, `UnknownHost` | The address could not be reached or resolved |
| `RemoteConnectionClose` | The server closed the connection (a kick, a shutdown, a rejected token) |
| `ConnectionRejected` | The server refused the handshake, for example because of a wrong connection key |
| `InvalidProtocol` | The server speaks a different transport protocol version |
| `SecureHandshakeTimeout` | The secure handshake got no answer within `Handshake Timeout`: a wrong server key or pre-shared key, a server without encryption, or a lost network |
| `SecureHandshakeFailed` | The answer of the server could not be verified, or the encrypted session was closed |
| `RateLimited` | The server disconnected you for sending too many messages or bytes, or a message that was too large (the transport adds extra data after the reason, for example `RemoteConnectionClose:RateLimited`) |
| `AuthTimeout`, `TooManyAuthAttempts` | The server disconnected you because you did not authenticate in time, or failed too often |
| `Failed to open a UDP socket` | The local socket could not be opened |
| A server message | An authentication failure passes the server's text (for example for a wrong `AppId`) |

Most strings are LiteNetLib's `DisconnectReason` names. When the connection drops, every pending operation fails with `NetPulseReturnCode.Disconnected` (-101), the room and the room list are cleared, network objects are destroyed locally, and the state returns to `Disconnected`. `OnLeftRoom` is raised first if you were in a room, then `OnDisconnected`.

To reconnect, call `Pulse.Connect()` again. A common pattern is to start it from `OnDisconnected` after a short delay.

## Operation errors

Every request has a timeout (`Request Timeout`, 10 seconds by default). Client-side failures use `NetPulseReturnCode`:

| Code | Name | Meaning |
|---|---|---|
| -100 | `Timeout` | The server did not answer in time |
| -101 | `Disconnected` | The connection was lost or was never up |
| -102 | `InvalidState` | The call is not allowed in the current state, for example joining a room while `Disconnected` |

Server answers use the codes listed in [codes and keys](../reference/codes.md), for example `AuthenticationFailed` (-32), `VersionMismatch` (-30) and `InvalidAppId` (-31). Awaited operations throw `NetPulseException` with a `ReturnCode`; others expose `Error`.

## Connection quality and replacing the transport

`Pulse.RoundTripTimeMs` is the round-trip time of the UDP link. `Pulse.ServerTime` is the estimated server clock (see [ticks and time](ticks-and-time.md)).

`Pulse.UseTransport(INetTransport)` swaps the transport, for example for a WebSocket transport on WebGL. It must be called while disconnected and before `Connect()`. It resets the client, so register callbacks afterwards. The default transport is `LiteNetLibTransport`.
