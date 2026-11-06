# Wire protocol

This page describes what travels between a client and the server, for anyone writing a custom client. Unity users never need it. All numbers are from protocol version 1 (`Protocol.ProtocolVersion`). The names come from `NetPulse.Common`, a library that compiles on .NET Standard 2.1 and ships inside the Unity package. A .NET client can use it directly.

## Transport

NetPulse sends its messages over UDP with LiteNetLib 1.3.5. A custom client has to speak LiteNetLib's protocol, so in practice it is a .NET client that uses the same LiteNetLib version.

- The client connects to the server's UDP port (5055 by default) with a connection key (`NetPulse` by default). The key is checked by the transport.
- Each NetPulse message is one LiteNetLib packet, sent on the default channel.
- The delivery mode of a packet maps to a LiteNetLib method:

| `DeliveryMode` | Value | LiteNetLib method |
|---|---|---|
| `ReliableOrdered` | 0 | `ReliableOrdered` |
| `ReliableUnordered` | 1 | `ReliableUnordered` |
| `UnreliableSequenced` | 2 | `Sequenced` |
| `Unreliable` | 3 | `Unreliable` |

The server sends responses and most events with `ReliableOrdered`. Events raised by clients or plugins are sent with the `Delivery` the sender asked for. Pongs are `Unreliable`.

- The server drops a peer that is silent for `DisconnectTimeoutMs` (10 seconds by default). Keep calling `PollEvents`, which sends the transport's keep-alive messages.
- When the server disconnects a client on purpose, it puts a UTF-8 reason in the disconnect data. Known reasons are `Overloaded` and `AuthenticationFailed`.

## Primitives

All multi-byte numbers are little-endian.

| Name | Encoding |
|---|---|
| `byte`, `bool` | 1 byte. A bool is 0 or 1. |
| `short`, `ushort` | 2 bytes. |
| `int`, `uint`, `float` | 4 bytes. |
| `long`, `ulong`, `double` | 8 bytes. |
| `varuint` | 7 bits per byte, low bits first, the high bit marks "more bytes follow". At most 5 bytes. |
| `string` | `varuint` (UTF-8 byte count + 1), then the bytes. A prefix of 0 is `null`, a prefix of 1 is the empty string. |
| `bytes` | `varuint` length, then the bytes. |
| `Guid` | 16 bytes in the .NET layout. |

## Message framing

The first byte of every packet is the message kind.

| Kind | Value | Layout after the kind byte |
|---|---|---|
| OperationRequest | 1 | `opCode: byte`, `requestId: ushort`, `parameters` |
| OperationResponse | 2 | `opCode: byte`, `requestId: ushort`, `returnCode: short`, `debugMessage: string`, `parameters` |
| Event | 3 | `code: byte`, `parameters` |
| Ping | 4 | `clientTicks: long`, `serverTicks: long` |
| Pong | 5 | `clientTicks: long`, `serverMs: long` |

A response carries the `opCode` and `requestId` of its request. `returnCode` 0 is success; the others are in [Codes and keys](codes.md). `debugMessage` is human-readable text.

For a ping, send your own clock in `clientTicks` and 0 in `serverTicks`. The server answers with your value and its own time in milliseconds since it started. Subtract to estimate the round trip and the clock offset.

Request ids are yours to choose. Use a non-zero id for every request you want answered. The server answers every operation except `RaiseEvent`: that one gets an answer only when the id is non-zero or the event fails.

## Parameters

`parameters` is a dictionary from a one-byte key to a value:

```text
varuint count
count x ( key: byte, value )
```

The keys are the `ParamKey` constants, listed in [Codes and keys](codes.md#parameter-keys). Missing keys read as defaults. The server converts between numeric types when it reads a parameter, so sending an `int` where a `byte` is expected works as long as the value fits.

## Value types

Every value starts with a type code byte, followed by its payload.

| Code | Type | Payload |
|---|---|---|
| 0 | null | none |
| 1 | bool | 1 byte |
| 2 | byte | 1 byte |
| 3 | short | 2 bytes |
| 4 | int | 4 bytes |
| 5 | long | 8 bytes |
| 6 | float | 4 bytes |
| 7 | double | 8 bytes |
| 8 | string | `string` |
| 9 | byte[] | `bytes` |
| 10 | int[] | `varuint` count, then 4 bytes per element |
| 11 | float[] | `varuint` count, then 4 bytes per element |
| 12 | string[] | `varuint` count, then that many `string` values |
| 13 | object[] | `varuint` count, then that many values |
| 14 | dictionary | `varuint` count, then (`string` key, value) pairs |
| 15 | PropertyTable | same layout as a dictionary |
| 16 | Guid | 16 bytes |
| 17 | ParameterDictionary | a parameters block (count, then key and value pairs) |
| 18 | extended | `ushort` id, `varuint` length, then the payload |
| 100 to 199 | custom | `varuint` length, then the payload |

Values nest up to 16 levels deep. A declared count or length larger than the data left in the packet makes the packet invalid.

Codes 100 to 199 and 18 carry application types such as vectors. They are length-prefixed so the server can store and forward a type it does not know: it keeps the bytes as an `OpaqueValue` and writes them back unchanged. The Unity package registers `Vector2` (100), `Vector3` (101), `Quaternion` (102), `Color` (103), `Vector2Int` (104) and `Vector3Int` (105). In .NET, register your own with `NetTypeRegistry.Register` (codes 100 to 199) or `NetTypeRegistry.RegisterExtended` (ids 0 to 65535).

A malformed packet is logged and ignored.

## A session

1. Connect with the connection key.
2. Send `Authenticate` (op 1) with the AppId, protocol version, user id, nickname and optional token. Until it succeeds, every other operation returns `NotAuthenticated`.
3. Optionally send `JoinLobby` (op 2). The server answers and then sends the full room list as event 230. Later events 230 carry changes (`IsFullList` false) at the `RoomListIntervalMs` interval. `GetRoomList` (op 30) returns the full list in the response.
4. Create or join a room with op 10, 11, 12 or 13. Only one join may be in progress per peer.
5. Inside the room, use ops 14 to 26. Events arrive as they happen.

### Operations and their parameters

| Op | Name | Parameters |
|---|---|---|
| 1 | Authenticate | `AppId` (str), `ProtocolVersion` (int), `AppVersion` (str), `UserId` (str), `NickName` (str), `AuthToken` (str), `AuthData` (PropertyTable) |
| 2 | JoinLobby | `LobbyName` (str) |
| 3 | LeaveLobby | none |
| 10 | CreateRoom | `RoomName`, `Options` (ParameterDictionary), `PlayerProperties` (PropertyTable) |
| 11 | JoinRoom | `RoomName`, `PlayerProperties` |
| 12 | JoinOrCreateRoom | like CreateRoom |
| 13 | JoinRandomRoom | `ExpectedProperties`, `ExpectedMaxPlayers` (byte), `PlayerProperties`, `Options` (used if a room has to be created) |
| 14 | LeaveRoom | `BecomeInactive` (bool) |
| 20 | RaiseEvent | `EventCode` (byte), `Data`, `Receivers` (byte), `TargetActors` (int[]), `InterestGroup` (byte), `Caching` (byte), `Delivery` (byte), `ViewId` (int), `OwnershipMode` (byte), `ObjectFlags` (byte) |
| 21 | SetRoomProperties | `Properties`, `ExpectedProperties`, `Broadcast` (bool, default true) |
| 22 | SetPlayerProperties | `ActorId` (0 means yourself), `Properties`, `ExpectedProperties`, `Broadcast` |
| 23 | ChangeInterestGroups | `GroupsToAdd` (byte[]), `GroupsToRemove` (byte[]) |
| 24 | TransferOwnership | `ViewId`, `NewOwnerActorId` |
| 25 | RequestOwnership | `ViewId` |
| 26 | RegisterSceneObjects | `ViewIds` (int[]), `OwnershipModes` (byte[]) |
| 30 | GetRoomList | none |

`Options` for room creation is a parameters block with `MaxPlayers` (byte, 0 is unlimited), `IsVisible`, `IsOpen`, `Properties` (initial custom room properties), `LobbyProperties` (string[] of property keys shown in the room list), `Plugin`, `PlayerTtlMs` and `EmptyRoomTtlMs`.

`PlayerTtlMs` and `EmptyRoomTtlMs` are in milliseconds. With `PlayerTtlMs` other than 0, a player who disconnects, or leaves with `BecomeInactive`, stays in the room as an inactive actor and can rejoin with the same `UserId`. A positive value removes the inactive player after that time, a negative value keeps it until it rejoins. A positive `EmptyRoomTtlMs` keeps an empty room alive for that time.

### Responses

A successful `Authenticate` returns `UserId`, `NickName`, `ServerTimeTicks` (milliseconds since server start, despite the name) and `ProtocolVersion`.

The join operations (10 to 13) answer with the room state, or with an error code and no parameters:

| Key | Content |
|---|---|
| `RoomName` | The room name. |
| `ActorId` | Your actor id. |
| `MasterClientId` | Actor id of the master. |
| `Players` | `object[]` of parameter blocks, one per actor: `ActorId`, `NickName`, `UserId`, `Properties`, `IsInactive`. |
| `RoomProperties` | The room properties, including the reserved keys. |
| `ServerTimeTicks` | Server milliseconds. |
| `MaxPlayers`, `IsOpen`, `IsVisible` | Room settings. |
| `IsRejoin` | True when you took over an inactive actor. |
| `TickRate` | The server's `TickRate` setting. |

After the join response the server sends the cached events, then an `OwnershipChanged` event for each object whose owner differs from its creator. The other players receive `PlayerJoined` with your player block.

## Events

An event carries a code and a parameters block. Codes up to 199 are your own events, and the object events 240 to 245 are also raised by clients. Both arrive with this payload:

| Key | Content |
|---|---|
| `Sender` | Actor id of the sender (0 for the server). |
| `Data` | The value the sender passed. |
| `ViewId` | The view id, when there is one. |

The server's own events use different payloads:

| Code | Name | Payload keys |
|---|---|---|
| 230 | RoomList | `IsFullList`, `Rooms` (`object[]` of blocks with `RoomName`, `PlayerCount`, `MaxPlayers`, `IsOpen`, `Properties`), `RemovedRooms` (string[]) |
| 231 | PlayerJoined | a player block |
| 232 | PlayerLeft | `ActorId`, `IsInactive`, `MasterClientId` |
| 233 | RoomPropertiesChanged | `ActorId` (0), `Properties` (the delta, a null value means deleted), `Sender` |
| 234 | PlayerPropertiesChanged | `ActorId`, `Properties`, `Sender` |
| 235 | MasterClientChanged | `MasterClientId` |
| 236 | OwnershipChanged | `ViewId`, `NewOwnerActorId` (0 for a scene object means "the master"), `Sender` |
| 237 | OwnershipRequested | `ViewId`, `RequesterActorId`, sent to the owner |
| 250 | ErrorInfo | `ErrorCode`, `Message` |

Room and player property changes go to every player, including the one who made them.

### Receivers, caching and groups

`Receivers` is 0 Others, 1 All, 2 MasterClient or 3 Targets (the actor ids in `TargetActors`). A non-zero `InterestGroup` limits delivery to players who joined that group with `ChangeInterestGroups`; group 0 reaches everyone.

`Caching` is 0 DoNotCache, 1 AddToRoomCache, 2 AddToRoomCacheGlobal, 3 RemoveFromRoomCache or 4 RemoveFromCacheForActorsLeft. Cached events are replayed to players who join later. `AddToRoomCache` events are removed when their sender leaves for good, `AddToRoomCacheGlobal` events stay. Removing from the cache removes entries with the same event code, and with the same `ViewId` when you pass one.

### Network objects

Object events are ordinary `RaiseEvent` calls with a special code and a `ViewId`. The server validates them as described in [Security](../server/security.md#ownership-validation): instantiate (240) needs `ViewId / 1000` to equal your actor id and a mode in `OwnershipMode` (0 Fixed, 1 Takeover, 2 Request); `ObjectFlags` bit 0 marks an object that survives a scene change. Destroy (241) removes the object and its cached events. RPC (242), state sync (243), message (244) and master sync variable (245) are forwarded after their checks. The format of `Data` for these events belongs to the client and is opaque to the server.

When the room property `_scene` changes, the server removes every scene object and every object not marked as persistent, with their cached events.

## A minimal client

This .NET program connects, authenticates and joins a room using `NetPulse.Common` and LiteNetLib 1.3.5:

```xml title="WireClient.csproj"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="LiteNetLib" Version="1.3.5" />
    <ProjectReference Include="path\to\NetPulse_Server\src\NetPulse.Common\NetPulse.Common.csproj" />
  </ItemGroup>

</Project>
```

```csharp title="MinimalClient.cs"
using LiteNetLib;
using NetPulse.Common;

namespace WireExamples;

public sealed class MinimalClient
{
    private readonly NetWriter _writer = new();
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private NetPeer? _server;
    private ushort _nextRequestId;

    public MinimalClient()
    {
        _manager = new NetManager(_listener);
        _listener.PeerConnectedEvent += OnConnected;
        _listener.NetworkReceiveEvent += OnReceive;
    }

    public void Run(string host, int port, TimeSpan duration)
    {
        _manager.Start();
        _manager.Connect(host, port, Protocol.DefaultConnectionKey);
        var end = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < end)
        {
            _manager.PollEvents();
            Thread.Sleep(15);
        }

        _manager.Stop();
    }

    private void OnConnected(NetPeer peer)
    {
        _server = peer;
        var login = new AuthenticateRequest { UserId = "demo", NickName = "Demo" };
        Send(OpCode.Authenticate, login.ToParameters());
    }

    private void Send(byte opCode, ParameterDictionary parameters)
    {
        var request = new OperationRequest { OpCode = opCode, RequestId = ++_nextRequestId, Parameters = parameters };
        var packet = Protocol.Serialize(_writer, request);
        _server!.Send(packet.Array!, packet.Offset, packet.Count, DeliveryMethod.ReliableOrdered);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        var data = new NetReader(reader.GetRemainingBytesSegment());
        var kind = Protocol.ReadKind(data);

        if (kind == MessageKind.OperationResponse)
        {
            var response = Protocol.ReadOperationResponse(data);
            Console.WriteLine($"response op={response.OpCode} request={response.RequestId} code={response.ReturnCode}");

            if (response.OpCode == OpCode.Authenticate && response.ReturnCode == ReturnCode.Ok)
            {
                Send(OpCode.JoinOrCreateRoom, new JoinOrCreateRoomRequest { RoomName = "wire-demo" }.ToParameters());
            }
        }
        else if (kind == MessageKind.Event)
        {
            var received = Protocol.ReadEvent(data);
            Console.WriteLine($"event code={received.Code}");
        }

        reader.Recycle();
    }
}
```

Start it from a top-level `Program.cs`:

```csharp title="Program.cs"
using WireExamples;

new MinimalClient().Run("127.0.0.1", 5055, TimeSpan.FromSeconds(2));
```

Against a running server it prints:

```text
response op=1 request=1 code=0
response op=12 request=2 code=0
```

`Protocol.Serialize` returns a segment of the writer's internal buffer, so send it before you reuse the writer.
