namespace NetPulse.Common.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void RequestRoundTrips()
    {
        var writer = new NetWriter();
        var request = new OperationRequest
        {
            OpCode = OpCode.CreateRoom,
            RequestId = 513,
            Parameters = new CreateRoomRequest
            {
                RoomName = "arena",
                Options = new RoomOptions { MaxPlayers = 4, Plugin = "Sample", CustomProperties = new PropertyTable { ["map"] = "x" } },
                PlayerProperties = new PropertyTable { ["team"] = 1 }
            }.ToParameters()
        };

        var reader = new NetReader(Protocol.Serialize(writer, request));
        Assert.Equal(MessageKind.OperationRequest, Protocol.ReadKind(reader));
        var read = Protocol.ReadOperationRequest(reader);
        var typed = CreateRoomRequest.FromParameters(read.Parameters);

        Assert.Equal(OpCode.CreateRoom, read.OpCode);
        Assert.Equal(513, read.RequestId);
        Assert.Equal("arena", typed.RoomName);
        Assert.Equal(4, typed.Options.MaxPlayers);
        Assert.Equal("Sample", typed.Options.Plugin);
        Assert.True(typed.Options.IsOpen);
        Assert.Equal("x", typed.Options.CustomProperties.Get<string>("map"));
        Assert.Equal(1, typed.PlayerProperties.Get<int>("team"));
    }

    [Fact]
    public void ResponseWithNullDebugMessageRoundTrips()
    {
        var writer = new NetWriter();
        var response = new OperationResponse { OpCode = 11, RequestId = 7, ReturnCode = ReturnCode.RoomFull, Parameters = null };

        var reader = new NetReader(Protocol.Serialize(writer, response));
        Assert.Equal(MessageKind.OperationResponse, Protocol.ReadKind(reader));
        var read = Protocol.ReadOperationResponse(reader);

        Assert.Equal(ReturnCode.RoomFull, read.ReturnCode);
        Assert.Null(read.DebugMessage);
        Assert.Equal(0, read.Parameters.Count);
    }

    [Fact]
    public void JoinRoomResponseRoundTripsPlayers()
    {
        var response = new JoinRoomResponse
        {
            RoomName = "r",
            LocalActorId = 2,
            MasterClientId = 1,
            ServerTimeTicks = 12345,
            RoomProperties = new PropertyTable { ["a"] = 1 },
            Players = new[]
            {
                new PlayerInfo { ActorId = 1, NickName = "A", UserId = "u1", Properties = new PropertyTable() },
                new PlayerInfo { ActorId = 2, NickName = "B", UserId = "u2", IsInactive = true }
            }
        };

        var writer = new NetWriter();
        var packet = Protocol.Serialize(writer, new OperationResponse { Parameters = response.ToParameters() });
        var reader = new NetReader(packet);
        Protocol.ReadKind(reader);
        var read = JoinRoomResponse.FromParameters(Protocol.ReadOperationResponse(reader).Parameters);

        Assert.Equal(2, read.Players.Length);
        Assert.Equal("B", read.Players[1].NickName);
        Assert.True(read.Players[1].IsInactive);
        Assert.Equal(12345, read.ServerTimeTicks);
        Assert.Equal(1, read.RoomProperties.Get<int>("a"));
    }

    [Fact]
    public void RaiseEventKeepsOpaqueDataAndEnums()
    {
        var request = new RaiseEventRequest
        {
            EventCode = 5,
            Data = new OpaqueValue(101, new byte[] { 1, 2 }),
            Receivers = ReceiverGroup.Targets,
            TargetActors = new[] { 3, 4 },
            Caching = CachingOption.AddToRoomCacheGlobal,
            Delivery = DeliveryMode.UnreliableSequenced,
            ViewId = 1001
        };

        var writer = new NetWriter();
        writer.WriteObject(request.ToParameters());
        var read = RaiseEventRequest.FromParameters((ParameterDictionary)new NetReader(writer.AsSegment()).ReadObject());

        Assert.Equal(ReceiverGroup.Targets, read.Receivers);
        Assert.Equal(CachingOption.AddToRoomCacheGlobal, read.Caching);
        Assert.Equal(DeliveryMode.UnreliableSequenced, read.Delivery);
        Assert.Equal(new[] { 3, 4 }, read.TargetActors);
        Assert.Equal(1001, read.ViewId);
        Assert.Equal(new OpaqueValue(101, new byte[] { 1, 2 }), read.Data);
    }

    [Fact]
    public void PingPongRoundTrips()
    {
        var writer = new NetWriter();
        var reader = new NetReader(Protocol.SerializePingPong(writer, MessageKind.Pong, 11, 22));

        Assert.Equal(MessageKind.Pong, Protocol.ReadKind(reader));
        Protocol.ReadPingPong(reader, out var client, out var server);
        Assert.Equal(11, client);
        Assert.Equal(22, server);
    }

    [Fact]
    public void RoomListDataRoundTrips()
    {
        var data = new RoomListData
        {
            IsFullList = true,
            Rooms = new[] { new RoomInfo { Name = "a", PlayerCount = 1, MaxPlayers = 4, IsOpen = true, LobbyProperties = new PropertyTable { ["m"] = "z" } } },
            RemovedRooms = new[] { "old" }
        };

        var writer = new NetWriter();
        writer.WriteObject(data.ToParameters());
        var read = RoomListData.FromParameters((ParameterDictionary)new NetReader(writer.AsSegment()).ReadObject());

        Assert.True(read.IsFullList);
        Assert.Equal("a", read.Rooms[0].Name);
        Assert.Equal("z", read.Rooms[0].LobbyProperties.Get<string>("m"));
        Assert.Equal(new[] { "old" }, read.RemovedRooms);
    }
}

public sealed class PropertyTableTests
{
    [Fact]
    public void MergeAppliesNullAsRemoval()
    {
        var table = new PropertyTable { ["a"] = 1, ["b"] = 2 };
        table.Merge(new PropertyTable { ["a"] = null, ["c"] = 3 });

        Assert.False(table.ContainsKey("a"));
        Assert.Equal(2, table.Get<int>("b"));
        Assert.Equal(3, table.Get<int>("c"));
    }

    [Fact]
    public void MatchesImplementsCompareAndSet()
    {
        var table = new PropertyTable { ["score"] = 5, ["tags"] = new[] { "x" } };

        Assert.True(table.Matches(new PropertyTable { ["score"] = (byte)5 }));
        Assert.True(table.Matches(new PropertyTable { ["missing"] = null }));
        Assert.True(table.Matches(new PropertyTable { ["tags"] = new[] { "x" } }));
        Assert.False(table.Matches(new PropertyTable { ["score"] = 6 }));
        Assert.False(table.Matches(new PropertyTable { ["score"] = null }));
    }

    [Fact]
    public void GetConvertsNumericTypes()
    {
        var table = new PropertyTable { ["n"] = (byte)7 };

        Assert.Equal(7, table.Get<int>("n"));
        Assert.Equal(0, table.Get<int>("nope"));
        Assert.False(table.TryGet<string>("nope", out _));
    }
}
