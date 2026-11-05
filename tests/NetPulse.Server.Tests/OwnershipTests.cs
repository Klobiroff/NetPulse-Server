using Microsoft.Extensions.DependencyInjection;
using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Core;
using NetPulse.Server.Plugins;
using NetPulse.TestClient;

namespace NetPulse.Server.Tests;

public abstract class OwnershipTestBase : ServerTestBase
{
    protected const byte StateSync = EventCode.StateSync;

    protected static Task<OperationResponse> InstantiateAsync(BotClient client, int viewId, ObjectOwnershipMode mode = ObjectOwnershipMode.Fixed) =>
        client.RaiseEventAsync(EventCode.ObjectInstantiate, new byte[] { 1, 2, 3 }, ReceiverGroup.Others, CachingOption.AddToRoomCache, viewId: viewId, ownershipMode: mode);

    protected static Task<OperationResponse> SyncAsync(BotClient client, int viewId) =>
        client.RaiseEventAsync(StateSync, new byte[] { 9 }, ReceiverGroup.Others, viewId: viewId);

    protected static Task<OperationResponse> DestroyAsync(BotClient client, int viewId) =>
        client.RaiseEventAsync(EventCode.ObjectDestroy, null, ReceiverGroup.Others, CachingOption.RemoveFromRoomCache, viewId: viewId);


    protected async Task<(BotClient A, BotClient B, BotClient C)> RoomOfThreeAsync(string room = "room", RoomOptions? options = null)
    {
        var a = await JoinAsync(await ConnectAsync("a"), room, options);
        var b = await JoinAsync(await ConnectAsync("b"), room, options);
        var c = await JoinAsync(await ConnectAsync("c"), room, options);
        return (a, b, c);
    }

    protected static int OwnerSeenBy(BotClient client, int viewId) =>
        client.Events
            .Where(e => e.Code == EventCode.OwnershipChanged)
            .Select(e => OwnershipChangedData.FromParameters(e.Parameters))
            .Where(o => o.ViewId == viewId)
            .Select(o => o.NewOwnerActorId)
            .DefaultIfEmpty(-1)
            .Last();
}

public sealed class ObjectValidationTests : OwnershipTestBase
{
    [Fact]
    public async Task StateSyncIsAcceptedOnlyFromTheOwner()
    {
        var (a, b, c) = await RoomOfThreeAsync();
        Assert.Equal(ReturnCode.Ok, (await InstantiateAsync(a, 1001)).ReturnCode);

        var rejected = await SyncAsync(b, 1001);
        Assert.Equal(ReturnCode.Forbidden, rejected.ReturnCode);

        var accepted = await SyncAsync(a, 1001);
        Assert.Equal(ReturnCode.Ok, accepted.ReturnCode);

        await c.WaitForEventAsync(StateSync);
        await Task.Delay(100);
        Assert.Equal(1, c.CountEvents(StateSync));
        Assert.Equal(0, a.CountEvents(StateSync));
        Assert.True(Services.GetRequiredService<ServerMetrics>().OwnershipViolations >= 1);
    }

    [Fact]
    public async Task RejectionWithoutRequestIdIsReportedAsErrorInfoAndNeverDisconnects()
    {
        var (a, b, _) = await RoomOfThreeAsync();
        await InstantiateAsync(a, 1001);

        b.Send(new OperationRequest
        {
            OpCode = OpCode.RaiseEvent,
            RequestId = 0,
            Parameters = new RaiseEventRequest { EventCode = StateSync, Data = new byte[] { 1 }, ViewId = 1001 }.ToParameters()
        });

        var error = await b.WaitForEventAsync(EventCode.ErrorInfo);
        Assert.Equal(ReturnCode.Forbidden, ErrorInfoData.FromParameters(error.Parameters).ErrorCode);
        Assert.True(b.IsConnected);
    }

    [Fact]
    public async Task DestroyIsAllowedForOwnerAndMasterOnly()
    {
        var (a, b, c) = await RoomOfThreeAsync();
        await InstantiateAsync(b, 2001);
        await InstantiateAsync(a, 1001);

        Assert.Equal(ReturnCode.Forbidden, (await DestroyAsync(c, 2001)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await DestroyAsync(b, 1001)).ReturnCode);

        Assert.Equal(ReturnCode.Ok, (await DestroyAsync(a, 2001)).ReturnCode);
        await c.WaitForEventAsync(EventCode.ObjectDestroy);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        await late.WaitForEventAsync(EventCode.ObjectInstantiate);
        await Task.Delay(100);
        var views = late.Events.Where(e => e.Code == EventCode.ObjectInstantiate).Select(e => RoomEventData.FromParameters(e.Parameters).ViewId).ToArray();
        Assert.Equal(new[] { 1001 }, views);
    }

    [Fact]
    public async Task SpoofedViewIdOnInstantiateIsRejected()
    {
        var (a, b, _) = await RoomOfThreeAsync();

        Assert.Equal(ReturnCode.Forbidden, (await InstantiateAsync(b, 1001)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await InstantiateAsync(b, 500)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await InstantiateAsync(b, 2001)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await InstantiateAsync(b, 2001)).ReturnCode);

        await Task.Delay(100);
        Assert.Equal(1, a.CountEvents(EventCode.ObjectInstantiate));
    }
}

public sealed class OwnershipModeTests : OwnershipTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:OwnershipRequestTimeoutMs"] = "400"
    };

    [Fact]
    public async Task FixedObjectsCannotChangeOwner()
    {
        var (a, b, _) = await RoomOfThreeAsync();
        await InstantiateAsync(a, 1001, ObjectOwnershipMode.Fixed);

        Assert.Equal(ReturnCode.Forbidden, (await b.TransferOwnershipAsync(1001, 2)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await a.TransferOwnershipAsync(1001, 2)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await b.RequestOwnershipAsync(1001)).ReturnCode);
    }

    [Fact]
    public async Task TakeoverLetsAnyoneTakeButNotAssignOthers()
    {
        var (a, b, c) = await RoomOfThreeAsync();
        await InstantiateAsync(a, 1001, ObjectOwnershipMode.Takeover);

        Assert.Equal(ReturnCode.Forbidden, (await c.TransferOwnershipAsync(1001, 1)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await b.TransferOwnershipAsync(1001, 2)).ReturnCode);

        await a.WaitForEventAsync(EventCode.OwnershipChanged);
        await c.WaitForEventAsync(EventCode.OwnershipChanged);
        Assert.Equal(2, OwnerSeenBy(a, 1001));
        Assert.Equal(ReturnCode.Ok, (await SyncAsync(b, 1001)).ReturnCode);
        Assert.Equal(ReturnCode.Forbidden, (await SyncAsync(a, 1001)).ReturnCode);
    }

    [Fact]
    public async Task RequestModeIsForwardedToTheOwnerWhoApprovesByTransferring()
    {
        var (a, b, c) = await RoomOfThreeAsync();
        await InstantiateAsync(a, 1001, ObjectOwnershipMode.Request);

        Assert.Equal(ReturnCode.Forbidden, (await b.TransferOwnershipAsync(1001, 2)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await b.RequestOwnershipAsync(1001)).ReturnCode);

        var forwarded = OwnershipRequestedData.FromParameters((await a.WaitForEventAsync(EventCode.OwnershipRequested)).Parameters);
        Assert.Equal(1001, forwarded.ViewId);
        Assert.Equal(2, forwarded.RequesterActorId);
        Assert.Equal(0, c.CountEvents(EventCode.OwnershipRequested));

        Assert.Equal(ReturnCode.Ok, (await a.TransferOwnershipAsync(1001, 2)).ReturnCode);
        await b.WaitForEventAsync(EventCode.OwnershipChanged);
        Assert.Equal(ReturnCode.Ok, (await SyncAsync(b, 1001)).ReturnCode);
    }

    [Fact]
    public async Task UnansweredRequestTimesOutWithoutChange()
    {
        var (a, b, _) = await RoomOfThreeAsync();
        await InstantiateAsync(a, 1001, ObjectOwnershipMode.Request);
        await b.RequestOwnershipAsync(1001);
        await a.WaitForEventAsync(EventCode.OwnershipRequested);

        await Task.Delay(800);

        Assert.Equal(0, a.CountEvents(EventCode.OwnershipChanged));
        Assert.Equal(ReturnCode.Forbidden, (await SyncAsync(b, 1001)).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await SyncAsync(a, 1001)).ReturnCode);
    }

    [Fact]
    public async Task LateJoinerSeesTheTransferredOwner()
    {
        var (a, b, _) = await RoomOfThreeAsync();
        await InstantiateAsync(a, 1001, ObjectOwnershipMode.Takeover);
        await b.TransferOwnershipAsync(1001, 2);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        await late.WaitForEventAsync(EventCode.ObjectInstantiate);
        await late.WaitForEventAsync(EventCode.OwnershipChanged);

        Assert.Equal(2, OwnerSeenBy(late, 1001));
    }

    [Fact]
    public async Task LeavingDestroysOwnedObjectsKeepsTransferredOnesAndReturnsSceneObjectsToMaster()
    {
        var (a, b, c) = await RoomOfThreeAsync();
        await InstantiateAsync(b, 2001, ObjectOwnershipMode.Takeover);
        await InstantiateAsync(b, 2002, ObjectOwnershipMode.Fixed);
        await c.TransferOwnershipAsync(2001, 3);
        Assert.Equal(ReturnCode.Ok, (await a.RegisterSceneObjectsAsync(new[] { 5 }, new[] { (byte)ObjectOwnershipMode.Takeover })).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await b.TransferOwnershipAsync(5, 2)).ReturnCode);

        await b.LeaveRoomAsync();
        await a.WaitForEventAsync(e => e.Code == EventCode.OwnershipChanged && OwnershipChangedData.FromParameters(e.Parameters).ViewId == 5 && OwnershipChangedData.FromParameters(e.Parameters).NewOwnerActorId == 0);

        var late = await JoinAsync(await ConnectAsync("late"), "room");
        await late.WaitForEventAsync(e => e.Code == EventCode.OwnershipChanged);
        await Task.Delay(150);

        var views = late.Events.Where(e => e.Code == EventCode.ObjectInstantiate).Select(e => RoomEventData.FromParameters(e.Parameters).ViewId).ToArray();
        Assert.Equal(new[] { 2001 }, views);
        Assert.Equal(3, OwnerSeenBy(late, 2001));
        Assert.Equal(ReturnCode.Ok, (await SyncAsync(c, 2001)).ReturnCode);
    }

    [Fact]
    public async Task InactivePlayersKeepTheirObjectsUntilTheyReallyLeave()
    {
        var options = new RoomOptions { PlayerTtlMs = 400 };
        var a = await JoinAsync(await ConnectAsync("a"), "room", options);
        var b = await JoinAsync(await ConnectAsync("b"), "room", options);
        await InstantiateAsync(b, 2001);

        b.Dispose();
        await a.WaitForEventAsync(e => e.Code == EventCode.PlayerLeft && PlayerLeftData.FromParameters(e.Parameters).IsInactive);

        var during = await JoinAsync(await ConnectAsync("during"), "room");
        await during.WaitForEventAsync(EventCode.ObjectInstantiate);

        await a.WaitForEventAsync(e => e.Code == EventCode.PlayerLeft && !PlayerLeftData.FromParameters(e.Parameters).IsInactive);
        var after = await JoinAsync(await ConnectAsync("after"), "room");
        await Task.Delay(200);

        Assert.Equal(0, after.CountEvents(EventCode.ObjectInstantiate));
    }
}

public sealed class OwnershipPluginTests : OwnershipTestBase
{
    private readonly TaskCompletionSource<int> _seenOwner = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override void RegisterPlugins(PluginManager plugins) =>
        plugins.Register("Veto", new InlinePluginFactory(host => new VetoPlugin(host, _seenOwner)));

    [Fact]
    public async Task PluginCanReadTheRegistryAndVetoTransfers()
    {
        var options = new RoomOptions { Plugin = "Veto" };
        var (a, b, _) = await RoomOfThreeAsync("room", options);
        await InstantiateAsync(a, 1001, ObjectOwnershipMode.Takeover);

        var vetoed = await b.TransferOwnershipAsync(1001, 2);
        Assert.Equal(ReturnCode.PluginReportedError, vetoed.ReturnCode);

        await a.RaiseEventAsync(8, null);
        Assert.Equal(1, await _seenOwner.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class VetoPlugin : RoomPlugin
    {
        private readonly TaskCompletionSource<int> _seen;

        public VetoPlugin(IPluginHost host, TaskCompletionSource<int> seen)
            : base(host)
        {
            _seen = seen;
        }

        public override ValueTask OnBeforeTransferOwnershipAsync(IBeforeTransferOwnershipCallInfo info, CancellationToken ct)
        {
            info.Fail("transfers are disabled");
            return default;
        }

        public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
        {
            if (info.Request.EventCode == 8)
            {
                _seen.TrySetResult(Host.GetNetworkObject(1001)?.OwnerActorId ?? -1);
            }

            info.Continue();
            return default;
        }
    }
}
