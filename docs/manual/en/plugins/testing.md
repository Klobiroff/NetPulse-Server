# Testing plugins

There are three levels, from quick to thorough: run the server with bot clients, unit-test hooks against fakes, and run the real server in-process inside a test. This page uses one small plugin for all three.

```csharp title="ScoreCapPlugin.cs"
using NetPulse.Plugins;

namespace ScoreCap;

public sealed class ScoreCapPlugin : RoomPlugin
{
    public const byte ScoreEvent = 30;
    public const int MaxPoints = 100;

    public ScoreCapPlugin(IPluginHost host)
        : base(host)
    {
    }

    public override ValueTask OnRaiseEventAsync(IRaiseEventCallInfo info, CancellationToken ct)
    {
        if (info.Request.EventCode == ScoreEvent && info.Request.Data is int points && points > MaxPoints)
        {
            info.Fail($"At most {MaxPoints} points per event");
            return default;
        }

        info.Continue();
        return default;
    }
}

public sealed class ScoreCapFactory : IPluginFactory
{
    public ValueTask<PluginCreateResult> CreateAsync(IPluginHost host, string pluginName, IReadOnlyDictionary<string, string> config, CancellationToken ct) =>
        ValueTask.FromResult(PluginCreateResult.Ok(new ScoreCapPlugin(host)));
}
```

## Level 1: a real server and bots

Install the plugin as described in [Writing a plugin](writing-a-plugin.md), start the server and run the test client:

```bash
dotnet run --project tools/NetPulse.TestClient -- --bots 2 --room test --plugin ScoreCap --seconds 3
```

The `joined` lines show that the bots connected, authenticated and passed your join hooks. A failed hook shows up as `join failed (-20 <your message>)`. The client's final verdict is written for the sample plugin and reports `FAILED` for any other plugin, so read the lines instead of the exit code. The server log shows load errors, timeouts and exceptions. This level suits a first look. It cannot assert on what a plugin sent.

## Level 2: unit tests with fakes

Hooks are plain methods that receive an interface, so you can call them directly. Write a fake for `IPluginHost` and one for each call info you use. The fake records the decision and the events the plugin broadcast.

```csharp title="Fakes.cs"
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetPulse.Common;
using NetPulse.Plugins;

namespace ScoreCap.Tests;

public enum FakeOutcome
{
    Pending,
    Continued,
    Failed,
    Cancelled
}

public sealed class FakeRaiseEventCall : IRaiseEventCallInfo
{
    public FakeRaiseEventCall(byte code, object? data, int actorId = 1)
    {
        Request = new RaiseEventRequest { EventCode = code, Data = data };
        ActorId = actorId;
    }

    public int ActorId { get; }

    public RaiseEventRequest Request { get; }

    public FakeOutcome Outcome { get; private set; }

    public string? Message { get; private set; }

    public bool IsProcessed => Outcome != FakeOutcome.Pending;

    public void Continue() => Outcome = FakeOutcome.Continued;

    public void Fail(string message)
    {
        Outcome = FakeOutcome.Failed;
        Message = message;
    }

    public void Cancel() => Outcome = FakeOutcome.Cancelled;
}

public sealed class FakeHost : IPluginHost
{
    public List<(byte Code, object? Data)> Broadcasts { get; } = new();

    public string RoomName { get; set; } = "test-room";

    public IReadOnlyList<IActor> Actors { get; } = Array.Empty<IActor>();

    public int MasterClientId { get; set; } = 1;

    public PropertyTable RoomProperties { get; } = new();

    public ILogger Logger { get; } = NullLogger.Instance;

    public long ServerTimeMs { get; set; }

    public IReadOnlyCollection<INetworkObject> NetworkObjects { get; } = Array.Empty<INetworkObject>();

    public IActor? GetActor(int actorId) => null;

    public INetworkObject? GetNetworkObject(int viewId) => null;

    public void BroadcastEvent(byte code, object? data, ReceiverGroup receivers = ReceiverGroup.All, int[]? targets = null, CachingOption caching = CachingOption.DoNotCache, DeliveryMode delivery = DeliveryMode.ReliableOrdered, int senderActorId = 0) =>
        Broadcasts.Add((code, data));

    public void SetRoomProperties(PropertyTable props, bool broadcast = true) => RoomProperties.Merge(props);

    public void SetPlayerProperties(int actorId, PropertyTable props, bool broadcast = true)
    {
    }

    public void KickActor(int actorId, string reason)
    {
    }

    public void CloseRoom()
    {
    }

    public IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Action callback) => new NoopDisposable();

    public IDisposable CreateTimer(TimeSpan dueTime, TimeSpan period, Func<CancellationToken, ValueTask> callback) => new NoopDisposable();

    public void Enqueue(Action action) => action();

    public Task<HttpResponseMessage> HttpAsync(HttpRequestMessage request, CancellationToken ct = default) =>
        Task.FromException<HttpResponseMessage>(new InvalidOperationException("No HTTP in unit tests"));

    public void RunInBackground(Func<CancellationToken, Task> work)
    {
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
```

With the fakes in place, a test is short:

```csharp title="ScoreCapTests.cs"
namespace ScoreCap.Tests;

public sealed class ScoreCapTests
{
    [Fact]
    public async Task Event_within_the_cap_continues()
    {
        var plugin = new ScoreCapPlugin(new FakeHost());
        var call = new FakeRaiseEventCall(ScoreCapPlugin.ScoreEvent, 50);

        await plugin.OnRaiseEventAsync(call, CancellationToken.None);

        Assert.Equal(FakeOutcome.Continued, call.Outcome);
    }

    [Fact]
    public async Task Event_above_the_cap_fails_with_a_message()
    {
        var plugin = new ScoreCapPlugin(new FakeHost());
        var call = new FakeRaiseEventCall(ScoreCapPlugin.ScoreEvent, 500);

        await plugin.OnRaiseEventAsync(call, CancellationToken.None);

        Assert.Equal(FakeOutcome.Failed, call.Outcome);
        Assert.Contains("100", call.Message);
    }
}
```

Fakes cannot reproduce the room fiber, timeouts or real delivery. Keep logic that is worth testing in plain methods and keep hooks thin.

## Level 3: the real server in a test

`ServerHost.Build` creates the same host as `Program.cs`. A test can start it on a random port, register a plugin factory in code instead of a folder, and connect with `BotClient` from the test client project. The repository's own tests use this approach.

```csharp title="ServerTests.cs"
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetPulse.Common;
using NetPulse.Server.Hosting;
using NetPulse.Server.Networking;
using NetPulse.Server.Plugins;
using NetPulse.TestClient;

namespace ScoreCap.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private IHost? _host;
    private BotClient? _bot;

    public async Task InitializeAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["NetPulse:Port"] = "0",
            ["NetPulse:EnableConsole"] = "false",
            ["NetPulse:Plugins:HotReload"] = "false",
            ["NetPulse:Plugins:Directory"] = Path.Combine(Path.GetTempPath(), "netpulse-test-plugins"),
            ["Logging:LogLevel:Default"] = "Warning"
        };

        _host = ServerHost.Build(Array.Empty<string>(), settings);
        _host.Services.GetRequiredService<PluginManager>().Register("ScoreCap", new ScoreCapFactory());
        await _host.StartAsync();

        _bot = new BotClient("bot");
        await _bot.ConnectAsync("127.0.0.1", _host.Services.GetRequiredService<NetPulseServer>().Port);
        await _bot.AuthenticateAsync();
        await _bot.JoinOrCreateRoomAsync("room", new RoomOptions { Plugin = "ScoreCap" });
    }

    public async Task DisposeAsync()
    {
        _bot?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task Server_rejects_an_oversized_score_event()
    {
        var response = await _bot!.RaiseEventAsync(ScoreCapPlugin.ScoreEvent, 500);

        Assert.Equal(ReturnCode.PluginReportedError, response.ReturnCode);
    }

    [Fact]
    public async Task Server_accepts_a_small_score_event()
    {
        var response = await _bot!.RaiseEventAsync(ScoreCapPlugin.ScoreEvent, 5);

        Assert.Equal(ReturnCode.Ok, response.ReturnCode);
    }
}
```

`RaiseEventAsync` sets a request id, so the server always answers it. Real clients that raise events without a request id get no answer unless the event fails.

The project file for the tests references the SDK projects, the server and the test client:

```xml title="ScoreCap.Tests.csproj"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="path\to\NetPulse_Server\src\NetPulse.Server\NetPulse.Server.csproj" />
    <ProjectReference Include="path\to\NetPulse_Server\tools\NetPulse.TestClient\NetPulse.TestClient.csproj" />
  </ItemGroup>

</Project>
```

Put `ScoreCapPlugin.cs` in the same project, or reference your plugin project from it. Run the tests with `dotnet test`.
