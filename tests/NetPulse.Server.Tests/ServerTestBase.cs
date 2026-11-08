using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetPulse.Common;
using NetPulse.Common.Security;
using NetPulse.Plugins;
using NetPulse.Server.Hosting;
using NetPulse.Server.Networking;
using NetPulse.Server.Plugins;
using NetPulse.TestClient;

namespace NetPulse.Server.Tests;

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;

        public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _owner.Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

public abstract class ServerTestBase : IAsyncLifetime
{
    private readonly List<BotClient> _clients = new();
    private readonly CapturingLoggerProvider _logs = new();
    private IHost? _host;
    private bool _stopped;

    protected int Port => Services.GetRequiredService<NetPulseServer>().Port;

    protected IServiceProvider Services => _host!.Services;

    protected PluginManager Plugins => Services.GetRequiredService<PluginManager>();

    protected CapturingLoggerProvider Logs => _logs;

    protected virtual Dictionary<string, string?> Settings => new();

    protected static string SamplePluginDirectory => Path.Combine(AppContext.BaseDirectory, "sample-plugin");

    protected virtual void RegisterPlugins(PluginManager plugins)
    {
    }

    public async Task InitializeAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["NetPulse:Port"] = "0",
            ["NetPulse:EnableConsole"] = "false",
            ["NetPulse:RoomListIntervalMs"] = "100",
            ["NetPulse:Plugins:Directory"] = SamplePluginDirectory,
            ["NetPulse:Plugins:HotReload"] = "false",
            ["Logging:LogLevel:Default"] = "Warning"
        };

        foreach (var pair in Settings)
        {
            settings[pair.Key] = pair.Value;
        }

        _host = ServerHost.Build(Array.Empty<string>(), settings, services => services.AddSingleton<ILoggerProvider>(_logs));
        RegisterPlugins(Plugins);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        if (_host is not null)
        {
            await StopHostAsync();
            _host.Dispose();
        }
    }

    protected async Task StopHostAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        await _host!.StopAsync();
    }

    protected async Task<BotClient> ConnectAsync(string name, bool authenticate = true, string? appId = null, SecureClientSettings? security = null, int? port = null, string? connectionKey = null, string? authToken = null)
    {
        var client = new BotClient(name, security);
        _clients.Add(client);
        await client.ConnectAsync("127.0.0.1", port ?? Port, connectionKey ?? Protocol.DefaultConnectionKey);

        if (authenticate)
        {
            var response = await client.AuthenticateAsync(appId: appId, authToken: authToken);
            Assert.Equal(ReturnCode.Ok, response.ReturnCode);
        }

        return client;
    }

    protected static async Task<BotClient> JoinAsync(BotClient client, string room, RoomOptions? options = null)
    {
        var response = await client.JoinOrCreateRoomAsync(room, options);
        Assert.Equal(ReturnCode.Ok, response.ReturnCode);
        return client;
    }

    protected static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met in time");
    }

    protected sealed class InlinePluginFactory : IPluginFactory
    {
        private readonly Func<IPluginHost, IRoomPlugin> _create;

        public InlinePluginFactory(Func<IPluginHost, IRoomPlugin> create) => _create = create;

        public ValueTask<PluginCreateResult> CreateAsync(IPluginHost host, string pluginName, IReadOnlyDictionary<string, string> config, CancellationToken ct) =>
            ValueTask.FromResult(PluginCreateResult.Ok(_create(host)));
    }
}
