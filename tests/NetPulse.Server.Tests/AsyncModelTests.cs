using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Plugins;
using NetPulse.Server.Plugins;
using NetPulse.TestClient;

namespace NetPulse.Server.Tests;

public sealed class Probe
{
    private readonly List<int> _order = new();
    private int _sections;
    private int _callbacks;
    private int _maxSections;
    private int _maxCallbacks;
    private int _callbackRuns;

    public TaskCompletionSource GateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource LateRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource UnhandledSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource EnqueueRan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentContexts Contexts { get; } = new();

    public int MaxSections => Volatile.Read(ref _maxSections);

    public int MaxCallbacks => Volatile.Read(ref _maxCallbacks);

    public int CallbackRuns => Volatile.Read(ref _callbackRuns);

    public IReadOnlyList<int> Order
    {
        get
        {
            lock (_order)
            {
                return _order.ToArray();
            }
        }
    }

    public void Section()
    {
        var now = Interlocked.Increment(ref _sections);
        InterlockedMax(ref _maxSections, now);
        Thread.SpinWait(2000);
        Interlocked.Decrement(ref _sections);
        Contexts.Record(SynchronizationContext.Current);
    }

    public void CallbackStarted()
    {
        var now = Interlocked.Increment(ref _callbacks);
        InterlockedMax(ref _maxCallbacks, now);
        Interlocked.Increment(ref _callbackRuns);
    }

    public void CallbackFinished() => Interlocked.Decrement(ref _callbacks);

    public void Record(int value)
    {
        lock (_order)
        {
            _order.Add(value);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;

        do
        {
            current = Volatile.Read(ref target);
        }
        while (value > current && Interlocked.CompareExchange(ref target, value, current) != current);
    }

    public sealed class ConcurrentContexts
    {
        private readonly HashSet<SynchronizationContext?> _seen = new();

        public int Distinct
        {
            get
            {
                lock (_seen)
                {
                    return _seen.Count;
                }
            }
        }

        public bool SawNull
        {
            get
            {
                lock (_seen)
                {
                    return _seen.Contains(null);
                }
            }
        }

        public void Record(SynchronizationContext? context)
        {
            lock (_seen)
            {
                _seen.Add(context);
            }
        }
    }
}

public sealed class ProbePlugin : RoomPlugin
{
    private readonly Probe _probe;
    private readonly bool _startTimer;

    public ProbePlugin(IPluginHost host, Probe probe, bool startTimer = false)
        : base(host)
    {
        _probe = probe;
        _startTimer = startTimer;
    }

    public override ValueTask OnJoinAsync(IJoinCallInfo info, CancellationToken ct)
    {
        if (_startTimer)
        {
            Host.CreateTimer(TimeSpan.Zero, TimeSpan.FromMilliseconds(15), async token =>
            {
                _probe.CallbackStarted();
                await Task.Delay(50, token);
                _probe.CallbackFinished();
            });
        }

        info.Continue();
        return default;
    }

    public override async ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        switch (info.Request.EventCode)
        {
            case 1:
                _probe.GateEntered.TrySetResult();
                await _probe.Gate.Task;
                info.Continue();
                break;
            case 2:
                _probe.Section();
                await Task.Delay(1, ct);
                _probe.Section();
                await Task.Yield();
                _probe.Section();
                info.Continue();
                break;
            case 3:
                var number = (int)info.Request.Data!;
                await Task.Delay(Random.Shared.Next(0, 4), ct);
                _probe.Record(number);
                info.Continue();
                break;
            case 4:
                var late = info;
                _ = _probe.LateRelease.Task.ContinueWith(_ => late.Continue(), TaskScheduler.Default);
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
                break;
            case 7:
                Host.RunInBackground(async token =>
                {
                    await Task.Delay(300, token);
                    Host.Enqueue(() => _probe.EnqueueRan.TrySetResult());
                });
                info.Continue();
                break;
            case 5:
                await Task.Delay(10, ct);
                throw new InvalidOperationException("boom after await");
            default:
                info.Continue();
                break;
        }
    }

    public override ValueTask OnUnhandledExceptionAsync(Exception ex, ICallInfo? info)
    {
        _probe.UnhandledSeen.TrySetResult();
        return default;
    }
}

public sealed class CloseCountingPlugin : RoomPlugin
{
    private readonly int[] _counters;

    public CloseCountingPlugin(IPluginHost host, int[] counters)
        : base(host)
    {
        _counters = counters;
    }

    public override ValueTask OnBeforeCloseRoomAsync(IBeforeCloseRoomCallInfo info, CancellationToken ct)
    {
        Interlocked.Increment(ref _counters[0]);
        info.Continue();
        return default;
    }

    public override async ValueTask OnCloseRoomAsync(ICloseRoomCallInfo info, CancellationToken ct)
    {
        await Task.Yield();
        Interlocked.Increment(ref _counters[1]);
        info.Continue();
    }
}

public sealed class SlowAuthPlugin : IAuthPlugin
{
    public async Task<AuthResult> AuthenticateAsync(AuthRequest request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return AuthResult.Ok();
    }
}

public sealed class IsolationTests : ServerTestBase
{
    private readonly Probe _probe = new();

    protected override void RegisterPlugins(PluginManager plugins) =>
        plugins.Register("Probe", new InlinePluginFactory(host => new ProbePlugin(host, _probe)));

    [Fact]
    public async Task SlowHookInOneRoomDoesNotDelayOtherRoomsOrLobby()
    {
        var options = new RoomOptions { Plugin = "Probe" };
        var a = await JoinAsync(await ConnectAsync("a"), "room-a", options);
        var b1 = await JoinAsync(await ConnectAsync("b1"), "room-b", options);
        var b2 = await JoinAsync(await ConnectAsync("b2"), "room-b", options);
        var observer = await ConnectAsync("observer");

        var blocked = a.RaiseEventAsync(1, "slow");
        await _probe.GateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await b1.RaiseEventAsync(20, "fast", ReceiverGroup.Others);
        await b2.WaitForEventAsync(20, TimeSpan.FromSeconds(1));

        var list = await observer.RequestAsync(OpCode.GetRoomList, new ParameterDictionary(), TimeSpan.FromSeconds(1));
        Assert.Equal(ReturnCode.Ok, list.ReturnCode);
        Assert.False(blocked.IsCompleted);

        _probe.Gate.SetResult();
        Assert.Equal(ReturnCode.Ok, (await blocked).ReturnCode);
    }

    [Fact]
    public async Task MessageOrderIsPreservedAcrossAwaitingHooks()
    {
        var options = new RoomOptions { Plugin = "Probe" };
        var a = await JoinAsync(await ConnectAsync("a"), "room", options);
        var b = await JoinAsync(await ConnectAsync("b"), "room", options);
        var responses = new List<Task<OperationResponse>>();

        for (var i = 0; i < 40; i++)
        {
            responses.Add(a.RaiseEventAsync(3, i, ReceiverGroup.Others));
        }

        await Task.WhenAll(responses);

        Assert.Equal(Enumerable.Range(0, 40), _probe.Order);
        await Eventually(() => b.CountEvents(3) == 40);
        var received = b.Events.Where(e => e.Code == 3).Select(e => RoomEventData.FromParameters(e.Parameters).Data).Cast<int>();
        Assert.Equal(Enumerable.Range(0, 40), received);
    }

    [Fact]
    public async Task RoomCodeNeverRunsInParallelAndKeepsItsContextAfterAwait()
    {
        var options = new RoomOptions { Plugin = "Probe" };
        var clients = new List<BotClient>();

        for (var i = 0; i < 4; i++)
        {
            clients.Add(await JoinAsync(await ConnectAsync($"c{i}"), "room", options));
        }

        var tasks = clients.SelectMany(c => Enumerable.Range(0, 25).Select(_ => c.RaiseEventAsync(2, 1, ReceiverGroup.Others))).ToList();
        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(ReturnCode.Ok, r.ReturnCode));
        Assert.Equal(1, _probe.MaxSections);
        Assert.False(_probe.Contexts.SawNull);
        Assert.Equal(1, _probe.Contexts.Distinct);
    }

    [Fact]
    public async Task ExceptionAfterAwaitIsHandledLikeASynchronousOne()
    {
        var a = await JoinAsync(await ConnectAsync("a"), "room", new RoomOptions { Plugin = "Probe" });

        var response = await a.RaiseEventAsync(5, "x");

        Assert.Equal(ReturnCode.PluginReportedError, response.ReturnCode);
        await _probe.UnhandledSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ReturnCode.Ok, (await a.RaiseEventAsync(20, "still alive")).ReturnCode);
    }
}

public sealed class HookTimeoutTests : ServerTestBase
{
    private readonly Probe _probe = new();

    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Plugins:HookTimeoutMs"] = "300"
    };

    protected override void RegisterPlugins(PluginManager plugins) =>
        plugins.Register("Probe", new InlinePluginFactory(host => new ProbePlugin(host, _probe)));

    [Fact]
    public async Task TimedOutHookFailsWithWarningAndLateContinueIsIgnored()
    {
        var options = new RoomOptions { Plugin = "Probe" };
        var a = await JoinAsync(await ConnectAsync("a"), "room", options);
        var b = await JoinAsync(await ConnectAsync("b"), "room", options);

        var response = await a.RaiseEventAsync(4, "hang");

        Assert.Equal(ReturnCode.PluginReportedError, response.ReturnCode);
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("timed out"));

        _probe.LateRelease.SetResult();
        await Eventually(() => Logs.Entries.Any(e => e.Message.Contains("already completed")));

        await a.RaiseEventAsync(20, "after", ReceiverGroup.Others);
        await b.WaitForEventAsync(20);
    }
}

public sealed class TimerTests : ServerTestBase
{
    private readonly Probe _probe = new();

    protected override void RegisterPlugins(PluginManager plugins) =>
        plugins.Register("Probe", new InlinePluginFactory(host => new ProbePlugin(host, _probe, startTimer: true)));

    [Fact]
    public async Task AsyncTimerTicksNeverOverlap()
    {
        await JoinAsync(await ConnectAsync("a"), "room", new RoomOptions { Plugin = "Probe" });

        await Eventually(() => _probe.CallbackRuns >= 4);

        Assert.Equal(1, _probe.MaxCallbacks);
    }
}

public sealed class ShutdownTests : ServerTestBase
{
    private readonly int[] _counters = new int[2];

    protected override void RegisterPlugins(PluginManager plugins) =>
        plugins.Register("Counter", new InlinePluginFactory(host => new CloseCountingPlugin(host, _counters)));

    [Fact]
    public async Task GracefulShutdownClosesEveryRoomThroughPluginHooks()
    {
        var options = new RoomOptions { Plugin = "Counter" };

        for (var i = 0; i < 3; i++)
        {
            await JoinAsync(await ConnectAsync($"c{i}"), $"room{i}", options);
        }

        await StopHostAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, Volatile.Read(ref _counters[0]));
        Assert.Equal(3, Volatile.Read(ref _counters[1]));
    }
}

public sealed class AuthTimeoutTests : ServerTestBase
{
    protected override Dictionary<string, string?> Settings => new()
    {
        ["NetPulse:Plugins:AuthTimeoutMs"] = "200"
    };

    protected override void RegisterPlugins(PluginManager plugins) => plugins.AuthPlugin = new SlowAuthPlugin();

    [Fact]
    public async Task SlowAuthPluginYieldsAuthenticationFailed()
    {
        var client = await ConnectAsync("a", authenticate: false);

        var response = await client.AuthenticateAsync();

        Assert.Equal(ReturnCode.AuthenticationFailed, response.ReturnCode);
    }
}

public sealed class AsyncTimerSampleTests : ServerTestBase
{
    [Fact]
    public async Task SampleBeforeJoinAwaitsAndCanRejectNickname()
    {
        var options = new RoomOptions { Plugin = "NetPulse.Plugins.Sample" };
        var banned = await ConnectAsync("banned");
        var response = await banned.JoinOrCreateRoomAsync("room", options);

        Assert.Equal(ReturnCode.PluginReportedError, response.ReturnCode);
    }
}
