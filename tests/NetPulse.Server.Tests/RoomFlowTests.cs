using NetPulse.Common;

namespace NetPulse.Server.Tests;

public sealed class RoomFlowTests : ServerTestBase
{
    [Fact]
    public async Task OperationsRequireAuthentication()
    {
        var client = await ConnectAsync("anon", authenticate: false);
        var response = await client.JoinOrCreateRoomAsync("r");

        Assert.Equal(ReturnCode.NotAuthenticated, response.ReturnCode);
    }

    [Fact]
    public async Task PingReturnsServerTime()
    {
        var client = await ConnectAsync("a");
        client.SendPing();

        await Eventually(() => client.ServerTimeMs > 0);
    }

    [Fact]
    public async Task TwoClientsExchangeEvents()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await JoinAsync(await ConnectAsync("b"), "room");

        Assert.Equal(1, a.ActorId);
        Assert.Equal(2, b.ActorId);
        Assert.Equal(1, b.MasterClientId);

        var joined = await a.WaitForEventAsync(EventCode.PlayerJoined);
        Assert.Equal(2, PlayerInfo.FromParameters(joined.Parameters).ActorId);

        await a.RaiseEventAsync(20, "hello", ReceiverGroup.Others);
        var received = await b.WaitForEventAsync(20);
        var data = RoomEventData.FromParameters(received.Parameters);

        Assert.Equal("hello", data.Data);
        Assert.Equal(1, data.SenderActorId);
        await Task.Delay(100);
        Assert.Equal(0, a.CountEvents(20));
    }

    [Fact]
    public async Task ReceiverGroupsRouteCorrectly()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await JoinAsync(await ConnectAsync("b"), "room");
        var c = await JoinAsync(await ConnectAsync("c"), "room");

        await b.RaiseEventAsync(21, 1, ReceiverGroup.MasterClient);
        await b.RaiseEventAsync(22, 1, ReceiverGroup.All);
        await b.RaiseEventAsync(23, 1, ReceiverGroup.Targets, targets: new[] { 3 });

        await a.WaitForEventAsync(21);
        await c.WaitForEventAsync(23);
        await b.WaitForEventAsync(22);
        await Task.Delay(100);

        Assert.Equal(0, c.CountEvents(21));
        Assert.Equal(0, a.CountEvents(23));
        Assert.Equal(0, b.CountEvents(21));
    }

    [Fact]
    public async Task CachedEventsAreReplayedToLateJoiner()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        await a.RaiseEventAsync(30, "cached", ReceiverGroup.Others, CachingOption.AddToRoomCache);
        await a.RaiseEventAsync(31, "uncached", ReceiverGroup.Others);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        var replay = await late.WaitForEventAsync(30);

        Assert.Equal("cached", RoomEventData.FromParameters(replay.Parameters).Data);
        await Task.Delay(100);
        Assert.Equal(0, late.CountEvents(31));
    }

    [Fact]
    public async Task OpaqueCustomTypesAreCachedAndForwardedUntouched()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var opaque = new OpaqueValue(101, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
        await a.RaiseEventAsync(32, opaque, ReceiverGroup.Others, CachingOption.AddToRoomCache);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        var replay = await late.WaitForEventAsync(32);

        Assert.Equal(opaque, RoomEventData.FromParameters(replay.Parameters).Data);
    }

    [Fact]
    public async Task CacheOfLeavingActorIsRemovedUnlessGlobal()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await JoinAsync(await ConnectAsync("b"), "room");
        await b.RaiseEventAsync(40, "mine", ReceiverGroup.Others, CachingOption.AddToRoomCache);
        await b.RaiseEventAsync(41, "global", ReceiverGroup.Others, CachingOption.AddToRoomCacheGlobal);
        await a.WaitForEventAsync(41);
        await b.LeaveRoomAsync();
        await a.WaitForEventAsync(EventCode.PlayerLeft);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        await late.WaitForEventAsync(41);
        await Task.Delay(100);

        Assert.Equal(0, late.CountEvents(40));
    }

    [Fact]
    public async Task DestroyRemovesCachedInstantiateByViewId()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        await a.RaiseEventAsync(EventCode.ObjectInstantiate, "p1", ReceiverGroup.Others, CachingOption.AddToRoomCache, viewId: 1001);
        await a.RaiseEventAsync(EventCode.ObjectInstantiate, "p2", ReceiverGroup.Others, CachingOption.AddToRoomCache, viewId: 1002);
        await a.RaiseEventAsync(EventCode.ObjectDestroy, null, ReceiverGroup.Others, viewId: 1001);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        await late.WaitForEventAsync(EventCode.ObjectInstantiate);
        await Task.Delay(100);

        var viewIds = late.Events.Where(e => e.Code == EventCode.ObjectInstantiate).Select(e => RoomEventData.FromParameters(e.Parameters).ViewId).ToArray();
        Assert.Equal(new[] { 1002 }, viewIds);
    }

    [Fact]
    public async Task RemoveFromRoomCacheDropsEventsByCode()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        await a.RaiseEventAsync(50, "x", ReceiverGroup.Others, CachingOption.AddToRoomCache);
        await a.RaiseEventAsync(50, null, ReceiverGroup.Others, CachingOption.RemoveFromRoomCache);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        await Task.Delay(200);

        Assert.Equal(0, late.CountEvents(50));
    }

    [Fact]
    public async Task MasterClientHandsOverWhenMasterLeaves()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await JoinAsync(await ConnectAsync("b"), "room");
        var c = await JoinAsync(await ConnectAsync("c"), "room");

        await a.LeaveRoomAsync();
        var changed = await b.WaitForEventAsync(EventCode.MasterClientChanged);

        Assert.Equal(2, changed.Parameters.Get<int>(ParamKey.MasterClientId));
        await c.WaitForEventAsync(EventCode.MasterClientChanged);
        Assert.Equal(2, c.MasterClientId);
    }

    [Fact]
    public async Task MasterClientHandsOverOnDisconnect()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await JoinAsync(await ConnectAsync("b"), "room");

        a.Dispose();
        await b.WaitForEventAsync(EventCode.MasterClientChanged);
        Assert.Equal(2, b.MasterClientId);
    }

    [Fact]
    public async Task RoomMatchmakingErrorsAreReported()
    {
        var a = await ConnectAsync("a");
        var b = await ConnectAsync("b");

        var missing = await b.JoinRoomAsync("nope");
        Assert.Equal(ReturnCode.RoomNotFound, missing.ReturnCode);

        Assert.Equal(ReturnCode.Ok, (await a.CreateRoomAsync("one", new RoomOptions { MaxPlayers = 1 })).ReturnCode);
        Assert.Equal(ReturnCode.RoomAlreadyExists, (await b.CreateRoomAsync("one")).ReturnCode);
        Assert.Equal(ReturnCode.RoomFull, (await b.JoinRoomAsync("one")).ReturnCode);
        Assert.Equal(ReturnCode.AlreadyInRoom, (await a.JoinRoomAsync("one")).ReturnCode);
    }

    [Fact]
    public async Task JoinRandomUsesLobbyPropertyFilter()
    {
        var a = await ConnectAsync("a");
        var b = await ConnectAsync("b");
        var c = await ConnectAsync("c");

        var options = new RoomOptions
        {
            CustomProperties = new PropertyTable { ["map"] = "desert", ["secret"] = 1 },
            LobbyProperties = new[] { "map" }
        };

        await a.CreateRoomAsync("desert-room", options);
        await Task.Delay(50);

        var hit = await b.JoinRandomAsync(new PropertyTable { ["map"] = "desert" });
        Assert.Equal(ReturnCode.Ok, hit.ReturnCode);

        var miss = await c.JoinRandomAsync(new PropertyTable { ["map"] = "forest" });
        Assert.Equal(ReturnCode.NoMatchFound, miss.ReturnCode);

        var created = await c.JoinRandomAsync(new PropertyTable { ["map"] = "forest" }, new RoomOptions { MaxPlayers = 2 });
        Assert.Equal(ReturnCode.Ok, created.ReturnCode);
    }

    [Fact]
    public async Task ClosedRoomRejectsJoinsAndIsHiddenFromRandom()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await ConnectAsync("b");

        var closed = await a.SetRoomPropertiesAsync(new PropertyTable { [RoomPropertyKeys.IsOpen] = false });
        Assert.Equal(ReturnCode.Ok, closed.ReturnCode);

        Assert.Equal(ReturnCode.RoomClosed, (await b.JoinRoomAsync("room")).ReturnCode);
        await Task.Delay(50);
        Assert.Equal(ReturnCode.NoMatchFound, (await b.JoinRandomAsync()).ReturnCode);
    }

    [Fact]
    public async Task PropertiesSupportCompareAndSetAndDeletion()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await JoinAsync(await ConnectAsync("b"), "room");

        Assert.Equal(ReturnCode.Ok, (await a.SetRoomPropertiesAsync(new PropertyTable { ["turn"] = 1 }, new PropertyTable { ["turn"] = null })).ReturnCode);
        Assert.Equal(ReturnCode.PropertyCasFailed, (await b.SetRoomPropertiesAsync(new PropertyTable { ["turn"] = 2 }, new PropertyTable { ["turn"] = null })).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await b.SetRoomPropertiesAsync(new PropertyTable { ["turn"] = 2 }, new PropertyTable { ["turn"] = 1 })).ReturnCode);

        await Eventually(() => a.RoomProperties.Get<int>("turn") == 2);

        Assert.Equal(ReturnCode.Ok, (await a.SetRoomPropertiesAsync(new PropertyTable { ["turn"] = null })).ReturnCode);
        await Eventually(() => !a.RoomProperties.ContainsKey("turn"));
    }

    [Fact]
    public async Task PlayerPropertiesAreBroadcastAndProtected()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await JoinAsync(await ConnectAsync("b"), "room");

        Assert.Equal(ReturnCode.Ok, (await b.SetPlayerPropertiesAsync(new PropertyTable { ["team"] = "red" })).ReturnCode);
        var changed = await a.WaitForEventAsync(EventCode.PlayerPropertiesChanged);
        var data = PropertiesChangedData.FromParameters(changed.Parameters);

        Assert.Equal(2, data.ActorId);
        Assert.Equal("red", data.Properties.Get<string>("team"));

        var forbidden = await b.RequestAsync(
            OpCode.SetPlayerProperties,
            new SetPropertiesRequest { ActorId = 1, Properties = new PropertyTable { ["team"] = "x" } }.ToParameters());
        Assert.Equal(ReturnCode.Forbidden, forbidden.ReturnCode);
    }

    [Fact]
    public async Task InterestGroupsFilterDelivery()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var b = await JoinAsync(await ConnectAsync("b"), "room");
        var c = await JoinAsync(await ConnectAsync("c"), "room");
        await b.ChangeInterestGroupsAsync(new byte[] { 5 });

        await a.RaiseEventAsync(60, "grp", ReceiverGroup.Others, interestGroup: 5);
        await a.RaiseEventAsync(61, "all", ReceiverGroup.Others);

        await b.WaitForEventAsync(60);
        await c.WaitForEventAsync(61);
        await Task.Delay(100);

        Assert.Equal(0, c.CountEvents(60));
    }

    [Fact]
    public async Task LobbyDeliversFullListThenDeltas()
    {
        var a = await ConnectAsync("a");
        await JoinAsync(await ConnectAsync("host"), "first");
        var lobbyResponse = await a.JoinLobbyAsync();
        Assert.Equal(ReturnCode.Ok, lobbyResponse.ReturnCode);

        var full = RoomListData.FromParameters((await a.WaitForEventAsync(EventCode.RoomList)).Parameters);
        Assert.True(full.IsFullList);
        Assert.Contains(full.Rooms, r => r.Name == "first");

        a.ClearEvents();
        await JoinAsync(await ConnectAsync("host2"), "second");
        var delta = await a.WaitForEventAsync(e => e.Code == EventCode.RoomList && !RoomListData.FromParameters(e.Parameters).IsFullList);
        Assert.Contains(RoomListData.FromParameters(delta.Parameters).Rooms, r => r.Name == "second");
    }

    [Fact]
    public async Task EmptyRoomIsRemovedFromLobby()
    {
        var a = await ConnectAsync("a");
        await a.JoinLobbyAsync();
        var host = await JoinAsync(await ConnectAsync("host"), "temp");
        await a.WaitForEventAsync(e => e.Code == EventCode.RoomList && RoomListData.FromParameters(e.Parameters).Rooms.Any(r => r.Name == "temp"));

        await host.LeaveRoomAsync();
        await a.WaitForEventAsync(e => e.Code == EventCode.RoomList && RoomListData.FromParameters(e.Parameters).RemovedRooms.Contains("temp"));
    }

    [Fact]
    public async Task InactivePlayerCanRejoinWithinTtl()
    {
        var options = new RoomOptions { PlayerTtlMs = 5000 };
        var a = await JoinAsync(await ConnectAsync("a"), "room", options);
        var b = await ConnectAsync("b");
        await JoinAsync(b, "room");
        Assert.Equal(2, b.ActorId);

        b.Dispose();
        var left = PlayerLeftData.FromParameters((await a.WaitForEventAsync(EventCode.PlayerLeft)).Parameters);
        Assert.True(left.IsInactive);

        var b2 = await ConnectAsync("b2", authenticate: false);
        Assert.Equal(ReturnCode.Ok, (await b2.AuthenticateAsync(userId: "b")).ReturnCode);
        var join = await b2.JoinRoomAsync("room");

        Assert.Equal(ReturnCode.Ok, join.ReturnCode);
        Assert.Equal(2, b2.ActorId);
        Assert.True(JoinRoomResponse.FromParameters(join.Parameters).IsRejoin);
    }

    [Fact]
    public async Task InactivePlayerIsRemovedAfterTtl()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", new RoomOptions { PlayerTtlMs = 300 });
        var b = await JoinAsync(await ConnectAsync("b"), "room");

        b.Dispose();
        await a.WaitForEventAsync(e => e.Code == EventCode.PlayerLeft && PlayerLeftData.FromParameters(e.Parameters).IsInactive);
        await a.WaitForEventAsync(e => e.Code == EventCode.PlayerLeft && !PlayerLeftData.FromParameters(e.Parameters).IsInactive);
    }

    [Fact]
    public async Task EmptyRoomSurvivesForItsTtl()
    {
        var options = new RoomOptions { EmptyRoomTtlMs = 400 };
        var a = await JoinAsync(await ConnectAsync("a"), "room", options);
        await a.LeaveRoomAsync();

        var b = await ConnectAsync("b");
        Assert.Equal(ReturnCode.Ok, (await b.JoinRoomAsync("room")).ReturnCode);
        await b.LeaveRoomAsync();
        await Task.Delay(900);

        var c = await ConnectAsync("c");
        Assert.Equal(ReturnCode.RoomNotFound, (await c.JoinRoomAsync("room")).ReturnCode);
    }

    [Fact]
    public async Task ReservedEventCodesCannotBeRaisedByClients()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room");
        var response = await a.RaiseEventAsync(EventCode.MasterClientChanged, 1);

        Assert.Equal(ReturnCode.Forbidden, response.ReturnCode);
    }

}

public sealed class AuthenticationTests : ServerTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:AppId"] = "my-game"
    };

    [Fact]
    public async Task WrongAppIdIsRejected()
    {
        var client = await ConnectAsync("a", authenticate: false);
        var response = await client.AuthenticateAsync(appId: "other");

        Assert.Equal(ReturnCode.InvalidAppId, response.ReturnCode);
    }

    [Fact]
    public async Task ProtocolVersionMismatchIsRejected()
    {
        var client = await ConnectAsync("a", authenticate: false);
        var request = new AuthenticateRequest { AppId = "my-game", ProtocolVersion = 99 };
        var response = await client.RequestAsync(OpCode.Authenticate, request.ToParameters());

        Assert.Equal(ReturnCode.VersionMismatch, response.ReturnCode);
    }

    [Fact]
    public async Task CorrectAppIdIsAccepted()
    {
        var client = await ConnectAsync("a", authenticate: false);
        var response = await client.AuthenticateAsync(appId: "my-game");

        Assert.Equal(ReturnCode.Ok, response.ReturnCode);
        Assert.False(string.IsNullOrEmpty(AuthenticateResponse.FromParameters(response.Parameters).UserId));
    }
}
