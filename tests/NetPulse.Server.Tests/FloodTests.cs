using Microsoft.Extensions.DependencyInjection;
using NetPulse.Common;
using NetPulse.Server.Networking;
using NetPulse.Server.Plugins;
using NetPulse.TestClient;

namespace NetPulse.Server.Tests;

public abstract class FloodTestBase : ServerTestBase
{
    protected Probe Probe { get; } = new();

    protected static RoomOptions ProbeRoom => new() { Plugin = "Probe" };

    protected override void RegisterPlugins(PluginManager plugins) =>
        plugins.Register("Probe", new InlinePluginFactory(host => new ProbePlugin(host, Probe, startTimer: StartTimer)));

    protected virtual bool StartTimer => false;

    protected Peer ServerPeer(string userId) =>
        Services.GetRequiredService<NetPulseServer>().Peers.Single(p => p.UserId == userId);

    protected static void SendRaw(BotClient client, byte code, object data) =>
        client.Send(new OperationRequest
        {
            OpCode = OpCode.RaiseEvent,
            RequestId = 0,
            Parameters = new RaiseEventRequest { EventCode = code, Data = data, Receivers = ReceiverGroup.Others }.ToParameters()
        });
}

public sealed class PeerLimitTests : FloodTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:PeerQueueLimit"] = "8"
    };

    [Fact]
    public async Task FloodingPeerIsDisconnectedWhileNormalPeerLosesNothing()
    {
        var flooder = await JoinAsync(await ConnectAsync("flooder"), "room", ProbeRoom);
        var normal = await JoinAsync(await ConnectAsync("normal"), "room", ProbeRoom);
        var watcher = await JoinAsync(await ConnectAsync("watcher"), "room", ProbeRoom);

        var blocked = flooder.RaiseEventAsync(1, "block");
        await Probe.GateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 40; i++)
        {
            SendRaw(flooder, 20, i);
        }

        var pending = Enumerable.Range(0, 3).Select(i => normal.RaiseEventAsync(30, i, ReceiverGroup.Others)).ToList();

        await Eventually(() => flooder.DisconnectReason is not null);
        Assert.Contains("Overloaded", flooder.DisconnectReason);

        Probe.Gate.SetResult();
        var responses = await Task.WhenAll(pending);

        Assert.All(responses, r => Assert.Equal(ReturnCode.Ok, r.ReturnCode));
        await Eventually(() => watcher.CountEvents(30) == 3);
        Assert.True(normal.IsConnected);
        Assert.Null(normal.DisconnectReason);
        Assert.Equal(1, Services.GetRequiredService<NetPulse.Server.Core.ServerMetrics>().PeersOverloaded);
        await Task.WhenAny(blocked, Task.Delay(100));
    }
}

public sealed class StallTests : FloodTestBase
{
    [Fact]
    public async Task SlowHookDoesNotDisconnectANormalRatePeer()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", ProbeRoom);
        var b = await JoinAsync(await ConnectAsync("b"), "room", ProbeRoom);

        var blocked = a.RaiseEventAsync(1, "stall");
        await Probe.GateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pending = Enumerable.Range(0, 20).Select(i => b.RaiseEventAsync(30, i, ReceiverGroup.Others)).ToList();
        await Task.Delay(1000);
        Assert.True(b.IsConnected);

        Probe.Gate.SetResult();
        var responses = await Task.WhenAll(pending);

        Assert.All(responses, r => Assert.Equal(ReturnCode.Ok, r.ReturnCode));
        Assert.True(b.IsConnected);
        await Eventually(() => a.CountEvents(30) == 20);
        await blocked;
    }
}

public sealed class InternalPostTests : FloodTestBase
{
    protected override bool StartTimer => true;

    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:PeerQueueLimit"] = "8",
        ["NetPulse:RoomQueueCapacity"] = "16"
    };

    [Fact]
    public async Task TimersAndEnqueueKeepWorkingWhilePeersAreAtTheirLimit()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", ProbeRoom);
        var b = await JoinAsync(await ConnectAsync("b"), "room", ProbeRoom);

        await a.RaiseEventAsync(7, "start-background");
        var blocked = a.RaiseEventAsync(1, "block");
        await Probe.GateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 7; i++)
        {
            SendRaw(a, 20, i);
        }

        for (var i = 0; i < 8; i++)
        {
            SendRaw(b, 20, i);
        }

        await Eventually(() => ServerPeer("a").InFlight == 8 && ServerPeer("b").InFlight == 8);
        await Task.Delay(400);
        var runsBefore = Probe.CallbackRuns;

        Probe.Gate.SetResult();
        await Probe.EnqueueRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Eventually(() => Probe.CallbackRuns > runsBefore + 1);

        Assert.True(a.IsConnected);
        Assert.True(b.IsConnected);
        await blocked;
    }
}

public sealed class InFlightCounterTests : FloodTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Plugins:HookTimeoutMs"] = "200"
    };

    [Fact]
    public async Task CounterReturnsToZeroAfterExceptionsTimeoutsAndDisconnects()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", ProbeRoom);
        var leaver = await JoinAsync(await ConnectAsync("leaver"), "room", ProbeRoom);
        var peerA = ServerPeer("a");
        var peerLeaver = ServerPeer("leaver");

        Assert.Equal(ReturnCode.PluginReportedError, (await a.RaiseEventAsync(5, "throws")).ReturnCode);
        Assert.Equal(ReturnCode.PluginReportedError, (await a.RaiseEventAsync(4, "times out")).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(20, "normal")).ReturnCode);

        for (var i = 0; i < 5; i++)
        {
            SendRaw(leaver, 4, i);
        }

        await Eventually(() => peerLeaver.InFlight > 0);
        leaver.Dispose();

        await Eventually(() => peerA.InFlight == 0 && peerLeaver.InFlight == 0, 10000);
    }
}

public sealed class GlobalFullTests : FloodTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:PeerQueueLimit"] = "100",
        ["NetPulse:RoomQueueCapacity"] = "10"
    };

    [Fact]
    public async Task FullRoomQueueDisconnectsTheHeaviestPeerNotThePoster()
    {
        var heavy = await JoinAsync(await ConnectAsync("heavy"), "room", ProbeRoom);
        var light = await JoinAsync(await ConnectAsync("light"), "room", ProbeRoom);

        var blocked = heavy.RaiseEventAsync(1, "block");
        await Probe.GateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 8; i++)
        {
            SendRaw(heavy, 20, i);
        }

        await Eventually(() => ServerPeer("heavy").InFlight == 9);
        var first = light.RaiseEventAsync(30, "first", ReceiverGroup.Others);
        await Eventually(() => ServerPeer("light").InFlight == 1);

        var second = light.RaiseEventAsync(30, "second", ReceiverGroup.Others);

        await Eventually(() => heavy.DisconnectReason is not null);
        Assert.Contains("Overloaded", heavy.DisconnectReason);
        Assert.True(light.IsConnected);

        Probe.Gate.SetResult();
        Assert.Equal(ReturnCode.Ok, (await first).ReturnCode);
        Assert.Equal(ReturnCode.Ok, (await light.RaiseEventAsync(20, "still fine")).ReturnCode);
        Assert.True(light.IsConnected);
        await Task.WhenAny(blocked, second, Task.Delay(100));
    }
}
