using System.Collections.Concurrent;
using NetPulse.Common;
using NetPulse.Common.Security;

namespace NetPulse.TestClient;

public sealed class BotClient : IDisposable
{
    private readonly LiteNetTransport _transport = new();
    private readonly NetWriter _writer = new();
    private readonly object _sendGate = new();
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<OperationResponse>> _pending = new();
    private readonly List<EventData> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pollTask;
    private int _nextRequestId;

    public BotClient(string name, SecureClientSettings? security = null)
    {
        Name = name;
        _transport.ConfigureSecurity(security);
        _transport.Connected += () => _connected.TrySetResult();
        _transport.Disconnected += reason =>
        {
            _connected.TrySetException(new InvalidOperationException($"Disconnected: {reason}"));
            DisconnectReason = reason;
        };
        _transport.DataReceived += OnData;
    }

    public string Name { get; }

    public string? DisconnectReason { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    public string NickName { get; private set; } = string.Empty;

    public int ActorId { get; private set; }

    public int MasterClientId { get; private set; }

    public long ServerTimeMs { get; private set; }

    public bool IsConnected => _transport.IsConnected;

    public bool IsEncrypted => _transport.IsEncrypted;

    public SecurityCounters SecurityCounters => _transport.Counters;

    public byte[]? HandshakeHash => _transport.HandshakeHash;

    public string? RejoinToken { get; private set; }

    public PropertyTable RoomProperties { get; private set; } = new();

    public IReadOnlyList<EventData> Events
    {
        get
        {
            lock (_events)
            {
                return _events.ToArray();
            }
        }
    }

    public async Task ConnectAsync(string host, int port, string connectionKey = Protocol.DefaultConnectionKey, TimeSpan? timeout = null)
    {
        _transport.Connect(host, port, connectionKey);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        await _connected.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
    }

    public async Task<OperationResponse> AuthenticateAsync(string? userId = null, string? appId = null, string? authToken = null)
    {
        var request = new AuthenticateRequest { AppId = appId, UserId = userId ?? Name, NickName = Name, AuthToken = authToken };
        var response = await RequestAsync(OpCode.Authenticate, request.ToParameters());

        if (response.ReturnCode == ReturnCode.Ok)
        {
            var data = AuthenticateResponse.FromParameters(response.Parameters);
            UserId = data.UserId;
            ServerTimeMs = data.ServerTimeTicks;
            NickName = data.NickName;
        }

        return response;
    }

    public Task<OperationResponse> JoinLobbyAsync() => RequestAsync(OpCode.JoinLobby, new JoinLobbyRequest().ToParameters());

    public Task<OperationResponse> CreateRoomAsync(string name, RoomOptions? options = null) =>
        JoinAsync(OpCode.CreateRoom, new CreateRoomRequest { RoomName = name, Options = options }.ToParameters());

    public Task<OperationResponse> JoinRoomAsync(string name) =>
        JoinAsync(OpCode.JoinRoom, new JoinRoomRequest { RoomName = name, RejoinToken = RejoinToken }.ToParameters());

    public Task<OperationResponse> JoinOrCreateRoomAsync(string name, RoomOptions? options = null) =>
        JoinAsync(OpCode.JoinOrCreateRoom, new JoinOrCreateRoomRequest { RoomName = name, Options = options, RejoinToken = RejoinToken }.ToParameters());

    public Task<OperationResponse> JoinRandomAsync(PropertyTable? filter = null, RoomOptions? createOptions = null) =>
        JoinAsync(OpCode.JoinRandomRoom, new JoinRandomRequest { ExpectedProperties = filter, CreateOptions = createOptions }.ToParameters());

    public Task<OperationResponse> LeaveRoomAsync(bool becomeInactive = false) =>
        RequestAsync(OpCode.LeaveRoom, new LeaveRoomRequest { BecomeInactive = becomeInactive }.ToParameters());

    public Task<OperationResponse> RaiseEventAsync(
        byte code,
        object? data,
        ReceiverGroup receivers = ReceiverGroup.Others,
        CachingOption caching = CachingOption.DoNotCache,
        byte interestGroup = 0,
        int viewId = 0,
        int[]? targets = null,
        ObjectOwnershipMode ownershipMode = ObjectOwnershipMode.Fixed,
        byte objectFlags = 0)
    {
        var request = new RaiseEventRequest
        {
            EventCode = code,
            Data = data,
            Receivers = receivers,
            Caching = caching,
            InterestGroup = interestGroup,
            ViewId = viewId,
            TargetActors = targets,
            OwnershipMode = ownershipMode,
            ObjectFlags = objectFlags
        };

        return RequestAsync(OpCode.RaiseEvent, request.ToParameters());
    }

    public Task<OperationResponse> TransferOwnershipAsync(int viewId, int newOwner) =>
        RequestAsync(OpCode.TransferOwnership, new TransferOwnershipRequest { ViewId = viewId, NewOwnerActorId = newOwner }.ToParameters());

    public Task<OperationResponse> RequestOwnershipAsync(int viewId) =>
        RequestAsync(OpCode.RequestOwnership, new RequestOwnershipRequest { ViewId = viewId }.ToParameters());

    public Task<OperationResponse> RegisterSceneObjectsAsync(int[] viewIds, byte[] modes) =>
        RequestAsync(OpCode.RegisterSceneObjects, new RegisterSceneObjectsRequest { ViewIds = viewIds, Modes = modes }.ToParameters());

    public Task<OperationResponse> SetRoomPropertiesAsync(PropertyTable properties, PropertyTable? expected = null) =>
        RequestAsync(OpCode.SetRoomProperties, new SetPropertiesRequest { Properties = properties, ExpectedProperties = expected }.ToParameters());

    public Task<OperationResponse> SetPlayerPropertiesAsync(PropertyTable properties) =>
        RequestAsync(OpCode.SetPlayerProperties, new SetPropertiesRequest { Properties = properties }.ToParameters());

    public Task<OperationResponse> ChangeInterestGroupsAsync(byte[]? add, byte[]? remove = null) =>
        RequestAsync(OpCode.ChangeInterestGroups, new ChangeInterestGroupsRequest { Add = add, Remove = remove }.ToParameters());

    public async Task<OperationResponse> RequestAsync(byte opCode, ParameterDictionary parameters, TimeSpan? timeout = null)
    {
        var requestId = (ushort)Interlocked.Increment(ref _nextRequestId);

        if (requestId == 0)
        {
            requestId = (ushort)Interlocked.Increment(ref _nextRequestId);
        }

        var completion = new TaskCompletionSource<OperationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        Send(new OperationRequest { OpCode = opCode, RequestId = requestId, Parameters = parameters });
        return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5));
    }

    public void Send(OperationRequest request)
    {
        lock (_sendGate)
        {
            _transport.Send(Protocol.Serialize(_writer, request), DeliveryMode.ReliableOrdered);
        }
    }

    public void SendPing()
    {
        lock (_sendGate)
        {
            _transport.Send(Protocol.SerializePingPong(_writer, MessageKind.Ping, Environment.TickCount64, 0), DeliveryMode.Unreliable);
        }
    }

    public async Task<EventData> WaitForEventAsync(Func<EventData, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTime.UtcNow < deadline)
        {
            lock (_events)
            {
                var index = _events.FindIndex(e => predicate(e));

                if (index >= 0)
                {
                    return _events[index];
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"{Name}: expected event did not arrive");
    }

    public Task<EventData> WaitForEventAsync(byte code, TimeSpan? timeout = null) =>
        WaitForEventAsync(e => e.Code == code, timeout);

    public int CountEvents(byte code)
    {
        lock (_events)
        {
            return _events.Count(e => e.Code == code);
        }
    }

    public void ClearEvents()
    {
        lock (_events)
        {
            _events.Clear();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _pollTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _transport.Dispose();
    }

    private async Task<OperationResponse> JoinAsync(byte opCode, ParameterDictionary parameters)
    {
        var response = await RequestAsync(opCode, parameters);

        if (response.ReturnCode == ReturnCode.Ok)
        {
            var joined = JoinRoomResponse.FromParameters(response.Parameters);
            ActorId = joined.LocalActorId;
            MasterClientId = joined.MasterClientId;
            RoomProperties = joined.RoomProperties;
            RejoinToken = joined.RejoinToken;
        }

        return response;
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _transport.Poll();
            await Task.Delay(5, CancellationToken.None);
        }
    }

    private void OnData(ArraySegment<byte> data, DeliveryMode mode)
    {
        var reader = new NetReader(data);

        switch (Protocol.ReadKind(reader))
        {
            case MessageKind.OperationResponse:
                var response = Protocol.ReadOperationResponse(reader);

                if (_pending.TryRemove(response.RequestId, out var completion))
                {
                    completion.TrySetResult(response);
                }

                break;
            case MessageKind.Event:
                HandleEvent(Protocol.ReadEvent(reader));
                break;
            case MessageKind.Pong:
                Protocol.ReadPingPong(reader, out _, out var serverTicks);
                ServerTimeMs = serverTicks;
                break;
        }
    }

    private void HandleEvent(EventData data)
    {
        switch (data.Code)
        {
            case EventCode.RoomPropertiesChanged:
                RoomProperties.Merge(PropertiesChangedData.FromParameters(data.Parameters).Properties);
                break;
            case EventCode.MasterClientChanged:
                MasterClientId = data.Parameters.Get<int>(ParamKey.MasterClientId);
                break;
        }

        lock (_events)
        {
            _events.Add(data);
        }
    }
}
