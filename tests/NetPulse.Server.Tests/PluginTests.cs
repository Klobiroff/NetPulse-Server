using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Plugins;

namespace NetPulse.Server.Tests;

public sealed class SamplePluginTests : ServerTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Plugins:Settings:NetPulse.Plugins.Sample:ScorePerPoint"] = "2"
    };

    [Fact]
    public async Task SampleLoadsFromFolder()
    {
        Assert.Contains(Plugins.Plugins, p => p.Name == "NetPulse.Plugins.Sample");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PluginKeepsAuthoritativeScoreAndCancelsTheEvent()
    {
        var options = new RoomOptions { Plugin = "NetPulse.Plugins.Sample" };
        var a = await JoinAsync(await ConnectAsync("a"), "room", options);
        var b = await JoinAsync(await ConnectAsync("b"), "room", options);

        var response = await a.RaiseEventAsync(10, 5, ReceiverGroup.All);
        Assert.Equal(ReturnCode.Ok, response.ReturnCode);

        await Eventually(() => b.RoomProperties.Get<int>("score_1") == 10);
        Assert.Equal(0, b.CountEvents(10));
    }

    [Fact]
    public async Task PluginRejectsForbiddenEventWithReturnCode()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", new RoomOptions { Plugin = "NetPulse.Plugins.Sample" });
        var response = await a.RaiseEventAsync(99, "x");

        Assert.Equal(ReturnCode.PluginReportedError, response.ReturnCode);
        Assert.Contains("not allowed", response.DebugMessage);
    }

    [Fact]
    public async Task PluginTimerBroadcastsOnTheRoomFiber()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", new RoomOptions { Plugin = "NetPulse.Plugins.Sample" });

        await a.WaitForEventAsync(50, TimeSpan.FromSeconds(4));
        await a.WaitForEventAsync(e => e.Code == 50 && e.Parameters.Get<int>(ParamKey.Data) >= 2, TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task UnknownPluginFailsRoomCreation()
    {
        var a = await ConnectAsync("a");
        var response = await a.CreateRoomAsync("room", new RoomOptions { Plugin = "Nope" });

        Assert.Equal(ReturnCode.PluginNotFound, response.ReturnCode);
    }
}

public sealed class MisbehavingPluginTests : ServerTestBase
{
    protected override void RegisterPlugins(PluginManager plugins) =>
        plugins.Register("Bad", new InlinePluginFactory(host => new BadPlugin(host)));

    [Fact]
    public async Task ExceptionInPluginFailsTheCallButRoomKeepsWorking()
    {
        var options = new RoomOptions { Plugin = "Bad" };
        var a = await JoinAsync(await ConnectAsync("a"), "room", options);
        var b = await JoinAsync(await ConnectAsync("b"), "room", options);

        var boom = await a.RaiseEventAsync(1, "x");
        Assert.Equal(ReturnCode.PluginReportedError, boom.ReturnCode);

        var ok = await a.RaiseEventAsync(2, "y", ReceiverGroup.Others);
        Assert.Equal(ReturnCode.Ok, ok.ReturnCode);
        await b.WaitForEventAsync(2);
    }

    [Fact]
    public async Task MissingContinueIsTreatedAsFail()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", new RoomOptions { Plugin = "Bad" });
        var response = await a.RaiseEventAsync(3, "x");

        Assert.Equal(ReturnCode.PluginReportedError, response.ReturnCode);
    }

    [Fact]
    public async Task DeferredCallCompletesAfterAsyncWork()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", new RoomOptions { Plugin = "Bad" });
        var b = await JoinAsync(await ConnectAsync("b"), "room", new RoomOptions { Plugin = "Bad" });

        var response = await a.RaiseEventAsync(4, "later", ReceiverGroup.Others);

        Assert.Equal(ReturnCode.Ok, response.ReturnCode);
        await b.WaitForEventAsync(4);
    }

    [Fact]
    public async Task PluginCanRewriteEventsAndKickPlayers()
    {
        var options = new RoomOptions { Plugin = "Bad" };
        var a = await JoinAsync(await ConnectAsync("a"), "room", options);
        var b = await JoinAsync(await ConnectAsync("b"), "room", options);

        await a.RaiseEventAsync(5, "original", ReceiverGroup.Others);
        var rewritten = await b.WaitForEventAsync(5);
        Assert.Equal("rewritten", RoomEventData.FromParameters(rewritten.Parameters).Data);

        await a.RaiseEventAsync(6, "kick-b");
        await b.WaitForEventAsync(EventCode.ErrorInfo);
        await a.WaitForEventAsync(EventCode.PlayerLeft);
    }

    [Fact]
    public async Task ThrowingFactoryDoesNotBreakTheServer()
    {
        Plugins.Register("Broken", new InlinePluginFactory(_ => throw new InvalidOperationException("factory exploded")));
        var a = await ConnectAsync("a");

        var broken = await a.CreateRoomAsync("room", new RoomOptions { Plugin = "Broken" });
        Assert.Equal(ReturnCode.PluginReportedError, broken.ReturnCode);

        var healthy = await a.CreateRoomAsync("room2");
        Assert.Equal(ReturnCode.Ok, healthy.ReturnCode);
    }

    private sealed class BadPlugin : RoomPlugin
    {
        public BadPlugin(IPluginHost host)
            : base(host)
        {
        }

        public override async ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
        {
            switch (info.Request.EventCode)
            {
                case 1:
                    throw new InvalidOperationException("plugin bug");
                case 3:
                    return;
                case 4:
                    await Task.Delay(100, ct);
                    info.Continue();
                    return;
                case 5:
                    info.Request.Data = "rewritten";
                    info.Continue();
                    return;
                case 6:
                    Host.KickActor(2, "bye");
                    info.Continue();
                    return;
                default:
                    info.Continue();
                    return;
            }
        }
    }
}

public sealed class HotReloadTests : ServerTestBase
{
    private string _directory = string.Empty;

    protected override Dictionary<string, string?> Settings
    {
        get
        {
            _directory = Path.Combine(Path.GetTempPath(), "netpulse-hot-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(Path.Combine(SamplePluginDirectory, "NetPulse.Plugins.Sample"), Path.Combine(_directory, "NetPulse.Plugins.Sample"));

            return new()
            {
                ["NetPulse:Plugins:Directory"] = _directory,
                ["NetPulse:Plugins:HotReload"] = "true"
            };
        }
    }

    [Fact]
    public async Task NewRoomsGetNewVersionWhileOldRoomsKeepTheOldOne()
    {
        var options = new RoomOptions { Plugin = "NetPulse.Plugins.Sample" };
        var first = await JoinAsync(await ConnectAsync("a"), "old-room", options);
        var versionBefore = Plugins.Plugins.Single().Version;

        var manifest = Path.Combine(_directory, "NetPulse.Plugins.Sample", "plugin.json");
        await File.WriteAllTextAsync(manifest, await File.ReadAllTextAsync(manifest) + Environment.NewLine);

        await WaitUntil(() => Plugins.Plugins.Single().Version > versionBefore);

        var second = await JoinAsync(await ConnectAsync("b"), "new-room", options);
        var leases = Plugins.Plugins.Single().ActiveLeases;

        Assert.Equal(1, leases);
        await first.RaiseEventAsync(10, 5);
        await WaitUntil(() => first.RoomProperties.Get<int>("score_1") == 5);
        await second.RaiseEventAsync(10, 3);
        await WaitUntil(() => second.RoomProperties.Get<int>("score_1") == 3);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
    }
}
