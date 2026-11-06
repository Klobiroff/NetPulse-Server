using NetPulse.Common;
using NetPulse.TestClient;

namespace NetPulse.Server.Tests;

public sealed class SceneAndMessageTests : OwnershipTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:TickRate"] = "40"
    };

    private static PropertyTable Scene(string value) => new() { [RoomPropertyKeys.Scene] = value };

    [Fact]
    public async Task JoinResponseCarriesTheConfiguredTickRate()
    {
        var client = await ConnectAsync("a");
        var response = await client.JoinOrCreateRoomAsync("room");
        Assert.Equal(40, JoinRoomResponse.FromParameters(response.Parameters).TickRate);
    }

    [Fact]
    public async Task MessageEventIsRoutedLikeAUserEvent()
    {
        var (a, b, c) = await RoomOfThreeAsync();

        Assert.Equal(ReturnCode.Ok, (await b.RaiseEventAsync(EventCode.Message, new byte[] { 1, 0, 9 }, ReceiverGroup.Others)).ReturnCode);
        var received = await c.WaitForEventAsync(EventCode.Message);
        Assert.Equal(new byte[] { 1, 0, 9 }, (byte[])RoomEventData.FromParameters(received.Parameters).Data!);
        await a.WaitForEventAsync(EventCode.Message);
        Assert.Equal(0, b.CountEvents(EventCode.Message));

        Assert.Equal(ReturnCode.Ok, (await c.RaiseEventAsync(EventCode.Message, new byte[] { 2 }, ReceiverGroup.Targets, targets: new[] { b.ActorId })).ReturnCode);
        await b.WaitForEventAsync(EventCode.Message);
    }

    [Fact]
    public async Task MessageEventCanBeCached()
    {
        var (a, _, _) = await RoomOfThreeAsync();
        await a.RaiseEventAsync(EventCode.Message, new byte[] { 5 }, ReceiverGroup.Others, CachingOption.AddToRoomCache);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        await late.WaitForEventAsync(EventCode.Message);
    }

    [Fact]
    public async Task SyncVarSetIsAcceptedOnlyFromTheMasterForKnownViews()
    {
        var (a, b, c) = await RoomOfThreeAsync();
        await InstantiateAsync(b, 2001);

        var rejected = await b.RaiseEventAsync(EventCode.SyncVarSet, new byte[] { 1 }, ReceiverGroup.Others, viewId: 2001);
        Assert.Equal(ReturnCode.Forbidden, rejected.ReturnCode);

        var accepted = await a.RaiseEventAsync(EventCode.SyncVarSet, new byte[] { 1 }, ReceiverGroup.Others, viewId: 2001);
        Assert.Equal(ReturnCode.Ok, accepted.ReturnCode);
        await c.WaitForEventAsync(EventCode.SyncVarSet);
        await Task.Delay(100);
        Assert.Equal(1, c.CountEvents(EventCode.SyncVarSet));
        Assert.Equal(1, b.CountEvents(EventCode.SyncVarSet));
        Assert.Equal(0, a.CountEvents(EventCode.SyncVarSet));
    }

    [Fact]
    public async Task SyncVarSetForUnknownViewIsDropped()
    {
        var (a, _, c) = await RoomOfThreeAsync();

        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(EventCode.SyncVarSet, new byte[] { 1 }, ReceiverGroup.Others, viewId: 3001)).ReturnCode);
        await Task.Delay(150);
        Assert.Equal(0, c.CountEvents(EventCode.SyncVarSet));
    }

    [Fact]
    public async Task SyncVarSetForSceneViewIsAcceptedFromTheMaster()
    {
        var (a, _, c) = await RoomOfThreeAsync();

        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(EventCode.SyncVarSet, new byte[] { 1 }, ReceiverGroup.Others, viewId: 12)).ReturnCode);
        await c.WaitForEventAsync(EventCode.SyncVarSet);
    }

    [Fact]
    public async Task OtherReservedEventCodesRemainForbidden()
    {
        var (a, _, _) = await RoomOfThreeAsync();
        Assert.Equal(ReturnCode.Forbidden, (await a.RaiseEventAsync(246, null)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await a.RaiseEventAsync(EventCode.OwnershipChanged, null)).ReturnCode);
    }

    [Fact]
    public async Task OnlyTheMasterMaySetTheSceneProperty()
    {
        var (a, b, c) = await RoomOfThreeAsync();

        Assert.Equal(ReturnCode.Forbidden, (await b.SetRoomPropertiesAsync(Scene("1:Arena"))).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await a.SetRoomPropertiesAsync(Scene("1:Arena"))).ReturnCode);

        var changed = await c.WaitForEventAsync(EventCode.RoomPropertiesChanged);
        Assert.Equal("1:Arena", PropertiesChangedData.FromParameters(changed.Parameters).Properties![RoomPropertyKeys.Scene]);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        Assert.Equal("1:Arena", late.RoomProperties[RoomPropertyKeys.Scene]);
    }

    [Fact]
    public async Task ChangingSceneRemovesSceneAndNonPersistentObjectsFromRegistryAndCache()
    {
        var (a, b, c) = await RoomOfThreeAsync();
        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(EventCode.ObjectInstantiate, new byte[] { 1 }, ReceiverGroup.Others, CachingOption.AddToRoomCache, viewId: 1001)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await b.RaiseEventAsync(EventCode.ObjectInstantiate, new byte[] { 2 }, ReceiverGroup.Others, CachingOption.AddToRoomCache, viewId: 2001, objectFlags: ObjectFlag.PersistAcrossScenes)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(EventCode.Rpc, new byte[] { 3 }, ReceiverGroup.Others, CachingOption.AddToRoomCache, viewId: 1001)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(EventCode.Rpc, new byte[] { 4 }, ReceiverGroup.Others, CachingOption.AddToRoomCache, viewId: 7)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(5, new byte[] { 5 }, ReceiverGroup.Others, CachingOption.AddToRoomCache)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await a.RegisterSceneObjectsAsync(new[] { 7 }, new[] { (byte)ObjectOwnershipMode.Takeover })).ReturnCode);

        Assert.Equal(ReturnCode.Ok, (await a.SetRoomPropertiesAsync(Scene("2:Next"))).ReturnCode);
        await c.WaitForEventAsync(EventCode.RoomPropertiesChanged);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        await late.WaitForEventAsync(EventCode.ObjectInstantiate);
        await late.WaitForEventAsync(5);
        await Task.Delay(150);

        var views = late.Events
            .Where(e => e.Code is EventCode.ObjectInstantiate or EventCode.Rpc)
            .Select(e => RoomEventData.FromParameters(e.Parameters).ViewId)
            .ToArray();
        Assert.Equal(new[] { 2001 }, views);

        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(EventCode.ObjectInstantiate, new byte[] { 1 }, ReceiverGroup.Others, viewId: 1001)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await b.RaiseEventAsync(EventCode.ObjectInstantiate, new byte[] { 2 }, ReceiverGroup.Others, viewId: 2001)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await c.RequestOwnershipAsync(7)).ReturnCode);
    }

    [Fact]
    public async Task SettingTheSameSceneValueDoesNotPurge()
    {
        var (a, _, _) = await RoomOfThreeAsync();
        await a.SetRoomPropertiesAsync(Scene("1:Arena"));
        Assert.Equal(ReturnCode.Ok, (await InstantiateAsync(a, 1001)).ReturnCode);

        await a.SetRoomPropertiesAsync(Scene("1:Arena"));
        Assert.Equal(ReturnCode.Forbidden, (await InstantiateAsync(a, 1001)).ReturnCode);

        await a.SetRoomPropertiesAsync(Scene("2:Other"));
        Assert.Equal(ReturnCode.Ok, (await InstantiateAsync(a, 1001)).ReturnCode);
    }
}
