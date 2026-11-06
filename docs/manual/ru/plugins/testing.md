# Тестирование плагинов

Есть три уровня, от быстрого к основательному: запустить сервер с ботами, проверить хуки модульными тестами на заглушках и запустить настоящий сервер внутри теста. На этой странице все три уровня показаны на одном небольшом плагине.

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

## Уровень 1: настоящий сервер и боты

Установите плагин, как описано в разделе [Написание плагина](writing-a-plugin.md), запустите сервер и тестовый клиент:

```bash
dotnet run --project tools/NetPulse.TestClient -- --bots 2 --room test --plugin ScoreCap --seconds 3
```

Строки `joined` показывают, что боты подключились, аутентифицировались и прошли хуки входа. Хук с ошибкой даёт строку `join failed (-20 <ваше сообщение>)`. Итоговый вердикт клиента написан для примера плагина и для любого другого плагина сообщает `FAILED`, поэтому читайте строки, а не код выхода. В логе сервера видны ошибки загрузки, тайм-ауты и исключения. Такой уровень подходит для первого знакомства. Проверить, что именно плагин отправил, он не позволяет.

## Уровень 2: модульные тесты с заглушками

Хуки представляют собой обычные методы, которые принимают интерфейс, поэтому их можно вызывать напрямую. Напишите заглушку для `IPluginHost` и по одной для каждого используемого вами `call info`. Заглушка записывает решение и события, которые плагин разослал.

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

Когда заглушки готовы, тест получается коротким:

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

Заглушки не воспроизводят файбер комнаты, тайм-ауты и настоящую рассылку. Логику, которую стоит тестировать, держите в обычных методах, а хуки делайте тонкими.

## Уровень 3: настоящий сервер в тесте

`ServerHost.Build` создаёт тот же хост, что и `Program.cs`. Тест может запустить его на случайном порту, зарегистрировать фабрику плагина в коде вместо папки и подключиться через `BotClient` из проекта тестового клиента. Собственные тесты репозитория работают именно так.

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

`RaiseEventAsync` задаёт идентификатор запроса, поэтому сервер всегда отвечает на него. Настоящие клиенты, отправляющие события без идентификатора запроса, ответа не получают, если только событие не завершилось ошибкой.

Файл проекта для тестов ссылается на проекты SDK, сервера и тестового клиента:

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

Положите `ScoreCapPlugin.cs` в тот же проект или сошлитесь из него на проект вашего плагина. Запускайте тесты командой `dotnet test`.
